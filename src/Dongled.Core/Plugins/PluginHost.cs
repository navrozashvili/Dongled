using Dongled.Core.Configuration;
using Dongled.Core.Engine;
using Dongled.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Plugins;

/// <summary>
/// Runs the plugins: loads them, gives each one a context and a lifecycle runner, and takes the
/// whole thing down again in the one order that satisfies what the SDK promises.
/// </summary>
/// <remarks>
/// <para>
/// Two orderings have to be right and neither is obvious from the outside, which is the entire
/// reason this type exists rather than leaving the sequence to whoever composes the app.
/// </para>
/// <para>
/// <strong>Starting.</strong> The engine has to be draining before any provider is started, and
/// getting this wrong is worse than it looks. The queue holds a bounded number of signals and a
/// provider that fills it waits for room. With no consumer running it waits <em>forever</em>:
/// measured, a provider publishing from inside <c>StartAsync</c> blocks on the 257th signal and
/// never returns, so <see cref="ProviderRunner"/> never reaches the line that would apply its
/// start deadline, and the whole host hangs rather than reporting a failed plugin. So
/// <see cref="RunAsync"/> starts <see cref="SwitchingEngine.RunAsync"/> first and only then loads
/// anything.
/// </para>
/// <para>
/// <strong>Stopping.</strong> Every provider is stopped before
/// <see cref="SwitchingEngine.CompleteInput"/> closes the queue, because
/// <see cref="Abstractions.IProviderContext"/> promises a provider may publish and log from inside
/// its own <c>StopAsync</c> and that only holds while the queue still accepts writes. Load
/// contexts are released last, once the engine has drained and nothing plugin-owned is referenced
/// any more.
/// </para>
/// <para>
/// This is composition, not containment. A loaded plugin runs in process with the user's full
/// privileges; nothing here changes that.
/// </para>
/// <para>
/// Only <see cref="IAsyncDisposable"/>, deliberately: stopping a provider is asynchronous and a
/// synchronous <c>Dispose</c> could only block on it. A dependency-injection container that has
/// built one of these must therefore be disposed with <c>await using</c>; disposing it
/// synchronously throws, naming this type.
/// </para>
/// </remarks>
public sealed class PluginHost : IAsyncDisposable
{
    private readonly IPluginLoader _loader;
    private readonly SwitchingEngine _engine;
    private readonly IConfigStore _configStore;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PluginHost> _logger;
    private readonly string _pluginsDirectory;

    private readonly List<(string Directory, ProviderRunner Runner)> _runners = [];
    private readonly List<LoadedPlugin> _loaded = [];

    private readonly TaskCompletionSource _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile Dictionary<string, LogLevel> _logLevels = new(StringComparer.OrdinalIgnoreCase);

    private bool _running;
    private bool _shutDown;

    /// <param name="loader">Decides which plugins may be loaded.</param>
    /// <param name="engine">The single consumer everything publishes into.</param>
    /// <param name="configStore">Where each plugin's trust entry and log level are read from.</param>
    /// <param name="time">Clock for each provider's start deadline.</param>
    /// <param name="loggerFactory">Gives each plugin its own log category.</param>
    /// <param name="pluginsDirectory">
    /// The plugins root, normally <see cref="StoragePaths.PluginsDirectory"/>.
    /// </param>
    public PluginHost(
        IPluginLoader loader,
        SwitchingEngine engine,
        IConfigStore configStore,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        string pluginsDirectory)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsDirectory);

        _loader = loader;
        _engine = engine;
        _configStore = configStore;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PluginHost>();
        _pluginsDirectory = pluginsDirectory;
    }

    /// <summary>
    /// What the loader decided about every directory under the plugins root. Empty until
    /// <see cref="Started"/> completes.
    /// </summary>
    public IReadOnlyList<PluginLoadResult> Results { get; private set; } = [];

    /// <summary>
    /// Completes once every plugin has been loaded and every loaded one has been started or has
    /// failed to start. The Plugins page waits on this before claiming to show live state.
    /// </summary>
    public Task Started => _started.Task;

    /// <summary>
    /// Where the provider from one plugin directory is in its lifecycle, or <see langword="null"/>
    /// if that directory produced no provider.
    /// </summary>
    /// <param name="directory">The directory name under the plugins root.</param>
    public ProviderRunState? StateOf(string directory)
    {
        foreach (var (name, runner) in _runners)
        {
            if (string.Equals(name, directory, StringComparison.OrdinalIgnoreCase))
            {
                return runner.State;
            }
        }

        return null;
    }

    /// <summary>
    /// Re-read the per-plugin log levels, which the UI calls after saving, alongside
    /// <see cref="SwitchingEngine.RequestConfigurationReload"/>.
    /// </summary>
    /// <remarks>
    /// The level is looked up on every log call so a change takes effect without restarting a
    /// provider, and this is where the looked-up values come from. Reading the file on each call
    /// instead would put a disk read behind every line a plugin logs.
    /// </remarks>
    public void ReloadConfiguration()
    {
        var levels = new Dictionary<string, LogLevel>(StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in _configStore.Load().Plugins)
        {
            levels[plugin.Directory] = plugin.LogLevel;
        }

        _logLevels = levels;
    }

    /// <summary>
    /// Run every approved plugin until <paramref name="ct"/> is cancelled, then shut everything
    /// down in order. Returns once the engine has drained.
    /// </summary>
    /// <param name="ct">Cancel to shut down.</param>
    /// <exception cref="InvalidOperationException">This host has already been run.</exception>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_running)
        {
            throw new InvalidOperationException(
                "This plugin host has already been run. Starting a second set of providers would give two runners to one plugin.");
        }

        _running = true;

        // Started before anything is loaded: a provider that publishes into a queue nobody is
        // draining waits until its start deadline expires. Not awaited here; it ends when the
        // input is completed during shutdown.
        var draining = _engine.RunAsync(CancellationToken.None);

        try
        {
            await LoadAndStartAsync(ct);

            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = ct.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                idle);
            await idle.Task;
        }
        catch (OperationCanceledException)
        {
            // The ordinary way this returns.
        }
        finally
        {
            await ShutDownAsync(draining);
        }
    }

    /// <summary>
    /// Shut down if <see cref="RunAsync"/> has not already done so. Safe to call after it has.
    /// </summary>
    public async ValueTask DisposeAsync() => await ShutDownAsync(draining: null);

    private async Task LoadAndStartAsync(CancellationToken ct)
    {
        ReloadConfiguration();

        Results = _loader.LoadAll(_pluginsDirectory, _configStore.Load().Plugins);

        foreach (var result in Results)
        {
            if (result.Plugin is null)
            {
                continue;
            }

            _loaded.Add(result.Plugin);

            var directory = result.Directory;

            // Reading Metadata is a call into plugin code, so it can throw. ProviderRunner guards
            // its own read the same way; this one happens first because the context needs an id.
            string? providerId;
            try
            {
                providerId = result.Plugin.Provider.Metadata?.Id;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Plugin {Directory} could not report its metadata; its directory name is used instead.",
                    directory);
                providerId = null;
            }

            if (string.IsNullOrWhiteSpace(providerId))
            {
                providerId = directory;
            }

            var context = _engine.CreateProviderContext(providerId, () => LevelFor(directory));
            var runner = new ProviderRunner(
                result.Plugin.Provider,
                context,
                _time,
                _loggerFactory.CreateLogger($"Plugin.{directory}"));

            _runners.Add((directory, runner));
        }

        // Started together rather than one after another: one plugin that hangs would otherwise
        // delay every plugin behind it by the full ten-second deadline. Per-provider ordering is
        // the only ordering the SDK promises, and the engine preserves that regardless.
        await Task.WhenAll(_runners.Select(entry => entry.Runner.StartAsync(ct)));

        _started.TrySetResult();
    }

    private LogLevel LevelFor(string directory) =>
        _logLevels.TryGetValue(directory, out var level) ? level : LogLevel.Warning;

    private async Task ShutDownAsync(Task? draining)
    {
        if (_shutDown)
        {
            return;
        }

        _shutDown = true;

        // Anything waiting on Started is waiting for a set of providers that will never exist now.
        _started.TrySetResult();

        // 1. Stop every provider while the queue is still open, so a provider that publishes from
        //    inside its own cleanup call is not writing into a closed channel.
        foreach (var (directory, runner) in _runners)
        {
            try
            {
                await runner.StopAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stopping plugin {Directory} failed.", directory);
            }
        }

        // 2. Only now close the queue, and let whatever is in it be processed.
        _engine.CompleteInput();

        if (draining is not null)
        {
            try
            {
                await draining;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The switching engine ended with an error.");
            }
        }

        // 3. Last, because until the engine has drained it may still be holding descriptors that
        //    came from a plugin's assembly.
        foreach (var (_, runner) in _runners)
        {
            runner.Dispose();
        }

        foreach (var plugin in _loaded)
        {
            plugin.Dispose();
        }

        _runners.Clear();
        _loaded.Clear();
    }
}
