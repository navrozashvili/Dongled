using Dongled.Abstractions;
using Dongled.Core.Configuration;
using Dongled.Core.Engine;
using Dongled.Core.Pipeline;
using Dongled.Core.Plugins;
using Dongled.Core.Tests.Fakes;
using Dongled.Core.Tests.Pipeline;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Dongled.Core.Tests.Plugins;

public sealed class PluginHostTests
{
    [Fact]
    public async Task Every_loaded_plugin_is_started_and_what_it_publishes_reaches_the_engine()
    {
        using var engine = NewEngine();
        var provider = new RecordingProvider("fake.one");
        var loader = new StubPluginLoader(StubPluginLoader.Loaded("One", provider));

        await using var host = NewHost(engine, loader);
        using var stopping = new CancellationTokenSource();

        var run = host.RunAsync(stopping.Token);
        await host.Started;

        Assert.True(provider.Started);
        Assert.Equal(ProviderRunState.Running, host.StateOf("One"));

        await stopping.CancelAsync();
        await run;

        Assert.Contains(engine.KnownSources(), source => source.SourceId == "fake.one:source");
    }

    [Fact]
    public async Task A_provider_may_still_publish_from_inside_its_own_cleanup_call()
    {
        // The ordering contract, stated as a test. If the engine's input were completed before
        // providers were stopped, this publish would be dropped and the source would never appear.
        using var engine = NewEngine();
        var provider = new PublishesWhileStoppingProvider("fake.late");
        var loader = new StubPluginLoader(StubPluginLoader.Loaded("Late", provider));

        await using var host = NewHost(engine, loader);
        using var stopping = new CancellationTokenSource();

        var run = host.RunAsync(stopping.Token);
        await host.Started;

        await stopping.CancelAsync();
        await run;

        Assert.True(provider.PublishedWhileStopping);
        Assert.Contains(engine.KnownSources(), source => source.SourceId == "fake.late:farewell");
    }

    [Fact]
    public async Task A_plugin_that_did_not_load_gets_no_runner()
    {
        using var engine = NewEngine();
        var loader = new StubPluginLoader(
            new PluginLoadResult("Blocked", PluginLoadStatus.Unapproved, "not approved"));

        await using var host = NewHost(engine, loader);
        using var stopping = new CancellationTokenSource();

        var run = host.RunAsync(stopping.Token);
        await host.Started;

        Assert.Null(host.StateOf("Blocked"));
        Assert.Equal(PluginLoadStatus.Unapproved, Assert.Single(host.Results).Status);

        await stopping.CancelAsync();
        await run;
    }

    [Fact]
    public async Task A_provider_that_throws_on_start_does_not_stop_the_others_from_running()
    {
        using var engine = NewEngine();
        var good = new RecordingProvider("fake.good");
        var bad = new ThrowsOnStartProvider("fake.bad");

        var loader = new StubPluginLoader(
            StubPluginLoader.Loaded("Bad", bad),
            StubPluginLoader.Loaded("Good", good));

        await using var host = NewHost(engine, loader);
        using var stopping = new CancellationTokenSource();

        var run = host.RunAsync(stopping.Token);
        await host.Started;

        Assert.Equal(ProviderRunState.Failed, host.StateOf("Bad"));
        Assert.Equal(ProviderRunState.Running, host.StateOf("Good"));

        await stopping.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Every_provider_is_stopped_once_and_every_loaded_plugin_is_released_once()
    {
        using var engine = NewEngine();
        var provider = new RecordingProvider("fake.one");
        var entry = StubPluginLoader.Loaded("One", provider);
        var loader = new StubPluginLoader(entry);

        var host = NewHost(engine, loader);
        using var stopping = new CancellationTokenSource();

        var run = host.RunAsync(stopping.Token);
        await host.Started;

        await stopping.CancelAsync();
        await run;

        // Disposing after RunAsync already shut down must not stop anything a second time.
        await host.DisposeAsync();

        Assert.Equal(1, provider.StopCount);
        Assert.Equal(1, StubPluginLoader.UnloadCount(entry));

        // The counts above stay at one even with both of this type's idempotence guards removed,
        // because ProviderRunner.StopAsync and LoadedPlugin.Dispose are each exactly-once in their
        // own right. This assertion is the part that belongs to the host: once it has shut down it
        // holds no runner, so nothing keeps a stopped provider or its load context alive.
        Assert.Null(host.StateOf("One"));
    }

    [Fact]
    public async Task Running_twice_is_refused_rather_than_starting_a_second_set_of_providers()
    {
        using var engine = NewEngine();
        var loader = new StubPluginLoader(StubPluginLoader.Loaded("One", new RecordingProvider("fake.one")));

        await using var host = NewHost(engine, loader);
        using var stopping = new CancellationTokenSource();

        var run = host.RunAsync(stopping.Token);
        await host.Started;

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunAsync(stopping.Token));

        await stopping.CancelAsync();
        await run;
    }

    [Fact]
    public async Task A_plugins_configured_log_level_is_read_fresh_on_every_call()
    {
        // A level change in the UI takes effect without restarting the provider.
        var config = new AppConfig
        {
            Plugins = [new PluginConfig { Directory = "One", Enabled = true, LogLevel = LogLevel.None }],
        };

        using var engine = NewEngine();
        var provider = new RecordingProvider("fake.one");
        var loader = new StubPluginLoader(StubPluginLoader.Loaded("One", provider));

        await using var host = NewHost(engine, loader, config);
        using var stopping = new CancellationTokenSource();

        var run = host.RunAsync(stopping.Token);
        await host.Started;

        Assert.NotNull(provider.Context);
        Assert.False(provider.Context.Logger.IsEnabled(ProviderLogLevel.Warning));

        config.Plugins[0].LogLevel = LogLevel.Trace;
        host.ReloadConfiguration();

        Assert.True(provider.Context.Logger.IsEnabled(ProviderLogLevel.Warning));

        await stopping.CancelAsync();
        await run;
    }

    [Fact]
    public async Task A_plugin_with_no_configured_level_falls_back_to_warning()
    {
        using var engine = NewEngine();
        var provider = new RecordingProvider("fake.one");
        var loader = new StubPluginLoader(StubPluginLoader.Loaded("One", provider));

        await using var host = NewHost(engine, loader);
        using var stopping = new CancellationTokenSource();

        var run = host.RunAsync(stopping.Token);
        await host.Started;

        Assert.NotNull(provider.Context);
        Assert.True(provider.Context.Logger.IsEnabled(ProviderLogLevel.Warning));
        Assert.False(provider.Context.Logger.IsEnabled(ProviderLogLevel.Information));

        await stopping.CancelAsync();
        await run;
    }

    // One shared instance: it holds nothing, and constructing one per call trips CA2000 because
    // ILoggerFactory is IDisposable and the callee keeps the reference.
    private static readonly EnabledLoggerFactory Loggers = new();

    private static SwitchingEngine NewEngine() => new(
        new FakeAudioEndpointService(),
        new StubConfigStore(),
        new RecordingStateStore(),
        TimeProvider.System,
        Loggers);

    private static PluginHost NewHost(SwitchingEngine engine, IPluginLoader loader, AppConfig? config = null) => new(
        loader,
        engine,
        new StubConfigStore { Config = config ?? new AppConfig() },
        TimeProvider.System,
        Loggers,
        "the-loader-is-a-stub-so-this-path-is-never-read");

    /// <summary>
    /// A factory whose loggers say yes. <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger"/>
    /// reports every level disabled, and <see cref="ProviderLoggerAdapter"/> ands its own decision
    /// with the host logger's, so a null logger would make every level test pass vacuously.
    /// </summary>
    private sealed class EnabledLoggerFactory : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new RecordingLogger();

        public void Dispose()
        {
        }
    }

    private sealed class StubPluginLoader : IPluginLoader
    {
        private static readonly Dictionary<PluginLoadResult, StrongBox> Unloads = [];

        private readonly PluginLoadResult[] _results;

        public StubPluginLoader(params PluginLoadResult[] results) => _results = results;

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Reliability",
            "CA2000:Dispose objects before losing scope",
            Justification = "The LoadedPlugin is handed to the PluginLoadResult, which the host owns and disposes during shutdown. That handoff is what the test measures, and CA2000 cannot see ownership transfer through a constructor argument.")]
        public static PluginLoadResult Loaded(string directory, IAudioSourceProvider provider)
        {
            var unloads = new StrongBox();
            var result = new PluginLoadResult(
                directory,
                PluginLoadStatus.Loaded,
                "Loaded.",
                plugin: new LoadedPlugin(directory, provider, () => unloads.Value++));

            lock (Unloads)
            {
                Unloads[result] = unloads;
            }

            return result;
        }

        public static int UnloadCount(PluginLoadResult result)
        {
            lock (Unloads)
            {
                return Unloads[result].Value;
            }
        }

        public IReadOnlyList<PluginLoadResult> LoadAll(
            string pluginsDirectory,
            IReadOnlyList<PluginConfig> trust) => _results;

        internal sealed class StrongBox
        {
            public int Value { get; set; }
        }
    }

    private class RecordingProvider(string id) : IAudioSourceProvider
    {
        public ProviderMetadata Metadata { get; } = new(id, id, null, false);

        public IProviderContext? Context { get; private set; }

        public bool Started { get; private set; }

        public int StopCount { get; private set; }

        public virtual Task StartAsync(IProviderContext context, CancellationToken ct)
        {
            Context = context;
            Started = true;
            context.PublishSources([new AudioSourceDescriptor(Metadata.Id + ":source", Metadata.Id, null)]);
            return Task.CompletedTask;
        }

        public virtual Task StopAsync(CancellationToken ct)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class PublishesWhileStoppingProvider(string id) : RecordingProvider(id)
    {
        public bool PublishedWhileStopping { get; private set; }

        public override Task StopAsync(CancellationToken ct)
        {
            Context!.PublishSources([new AudioSourceDescriptor(Metadata.Id + ":farewell", "Farewell", null)]);
            PublishedWhileStopping = true;
            return base.StopAsync(ct);
        }
    }

    private sealed class ThrowsOnStartProvider(string id) : RecordingProvider(id)
    {
        public override Task StartAsync(IProviderContext context, CancellationToken ct) =>
            throw new InvalidOperationException("this provider is broken");
    }
}
