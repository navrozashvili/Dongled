using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dongled.App.Presentation;
using Dongled.App.Services;
using Dongled.App.Tray;
using Dongled.App.ViewModels;
using Dongled.Core;
using Dongled.Core.Audio;
using Dongled.Core.Configuration;
using Dongled.Core.Engine;
using Dongled.Core.Logging;
using Dongled.Core.Plugins;
using Dongled.Core.Updates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Dongled.App;

/// <summary>
/// The application object: builds the dependency-injection host, composes the window, and runs the
/// plugin host.
/// </summary>
/// <remarks>
/// <para>
/// Starting and stopping is left to <see cref="PluginHost.RunAsync"/>, which owns two orderings
/// that are easy to get wrong: the engine must be draining before any provider starts, and every
/// provider must have stopped before the engine's queue closes.
/// </para>
/// <para>
/// Closing the window hides it. Only the tray's Exit item ends the process.
/// </para>
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "An Application is created by the XAML runtime and nothing ever disposes one; there is no caller to implement IDisposable for. The token source is disposed on the one path that ends the process, in ShutDownAndExitAsync.")]
public partial class App : Application
{
    private readonly SingleInstance? _instance;
    private readonly LogRingBuffer _logBuffer = new();
    private readonly CancellationTokenSource _shutdown = new();

    private MainWindow? _window;
    private IHost? _host;
    private Task? _pluginHost;
    private bool _exiting;
    private bool _restarting;

    /// <summary>
    /// Used only by the XAML-generated entry point, which this build replaces and never runs. The
    /// real one is <see cref="App(SingleInstance)"/>.
    /// </summary>
    public App()
        : this(null)
    {
    }

    /// <param name="instance">
    /// The single-instance claim taken in <see cref="Program"/>, used to hear when a second copy
    /// asks for the window. Null only from the unused generated entry point.
    /// </param>
    internal App(SingleInstance? instance)
    {
        _instance = instance;
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _host = BuildHost();
        var services = _host.Services;

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<App>();

        // First, so a session with redirected paths is obvious from the top of the log.
        if (StoragePaths.IsRedirected)
        {
            logger.LogWarning(
                "Configuration and state are redirected by {Variable}; this session does not use the normal per-user folder.",
                StoragePaths.DataDirectoryVariable);
        }

        // Anything in staging is left over from an install that did not finish. Swept once, before
        // the window exists, so an install started from the Plugins page cannot race it.
        services.GetRequiredService<PluginInstaller>().SweepStaging();

        // Whatever the last update moved aside. In the background and with retries, because the copy
        // it replaced may still be finishing its exit and holding some of those files open.
        _ = Task.Run(() => CleanUpAfterUpdateAsync(logger));

        _window = CreateWindow(services);

        var settings = services.GetRequiredService<IConfigStore>().Load().App;

        // Before the window is shown, so it is never drawn in the wrong theme.
        ApplyTheme(settings.Theme);

        // Starting hidden means the window is never activated, and the tray icon is created
        // directly. If that fails the window is shown after all, because a process with nothing on
        // screen and no icon cannot be stopped.
        // A replacement started by this app was asked for from the window - a plugin change or an
        // update - so it comes back with its window rather than vanishing into the tray.
        if (!settings.StartMinimized || SingleInstance.StartedAsReplacement || !_window.TryStartHidden())
        {
            _window.ShowFromTray();
        }

        // A second copy's request arrives on a background thread.
        _instance?.ListenForActivation(() =>
            _window?.DispatcherQueue.TryEnqueue(() => _window?.ShowFromTray()));

        // Not awaited: it runs for the lifetime of the app and returns once shutdown has drained
        // the engine.
        _pluginHost = services.GetRequiredService<PluginHost>().RunAsync(_shutdown.Token);
    }

    /// <summary>Build the tray icon renderer, falling back to a plain icon if the asset cannot be loaded.</summary>
    /// <remarks>An icon is decoration; a missing or unreadable file must not stop the window opening.</remarks>
    private static ITrayIconRenderer CreateTrayIconRenderer()
    {
        try
        {
            return new BatteryTrayIconRenderer(Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIcon.ico"));
        }
        catch (Exception)
        {
            return new NullTrayIconRenderer();
        }
    }

    /// <summary>
    /// Shut down and start a fresh copy, so that plugin changes take effect. Plugins are loaded once,
    /// at startup.
    /// </summary>
    private void RequestRestart()
    {
        _restarting = true;
        RequestExit();
    }

    /// <summary>
    /// Start the process that takes over from this one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The new process starts while this one still holds the single-instance mutex.
    /// <see cref="SingleInstance.AwaitReleaseVariable"/> tells it to wait for the mutex rather than
    /// conclude that the app is already running.
    /// </para>
    /// <para>
    /// <c>UseShellExecute</c> must be false, because only then can the environment be set. The rest
    /// of the environment is inherited, which keeps a redirected configuration directory pointing
    /// at the same place.
    /// </para>
    /// </remarks>
    private static void StartReplacement()
    {
        try
        {
            // The apphost rather than the managed dll, which would not bring the Windows App SDK
            // bootstrapper with it.
            var path = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var start = new ProcessStartInfo(path) { UseShellExecute = false };
            start.Environment[SingleInstance.AwaitReleaseVariable] = "1";

            using var replacement = Process.Start(start);
        }
        catch (Exception)
        {
            // This copy is already committed to exiting. The user sees the app close and can start
            // it again.
        }
    }

    private MainWindow CreateWindow(IServiceProvider services)
    {
        var dispatcher = new DispatcherQueueUiDispatcher(DispatcherQueue.GetForCurrentThread());
        var configStore = services.GetRequiredService<IConfigStore>();
        var endpoints = services.GetRequiredService<IAudioEndpointService>();
        var engine = services.GetRequiredService<ISwitchingEngine>();
        var updates = services.GetRequiredService<IUpdateService>();

        var shell = new ShellViewModel(endpoints, engine, configStore, CreateTrayIconRenderer(), dispatcher);

        var viewModels = new ViewModelFactory(
            configStore,
            endpoints,
            engine,
            new PluginHostRuntime(services.GetRequiredService<PluginHost>()),
            new CorePluginInstaller(services.GetRequiredService<PluginInstaller>()),
            services.GetRequiredService<PluginSession>(),
            services.GetRequiredService<TimeProvider>(),
            _logBuffer,
            dispatcher,
            RequestRestart,
            ApplyTheme,
            shell.Refresh,
            updates);

        var window = new MainWindow(
            shell,
            viewModels,
            services.GetRequiredService<IWindowPlacementStore>(),
            services.GetRequiredService<ILogger<MainWindow>>(),
            RequestExit);

        // The only trigger for an update check: the user opening the window. The service does
        // nothing for a build from source, when the setting is off, or when it checked recently.
        window.Opened += (_, _) => updates.NotifyWindowOpened();

        return window;
    }

    /// <summary>Apply a theme to the live window.</summary>
    /// <remarks>
    /// Set on the window's root element, because <see cref="Application.RequestedTheme"/> throws
    /// once a window exists.
    /// </remarks>
    private void ApplyTheme(AppTheme theme)
    {
        if (_window?.Content is not FrameworkElement root)
        {
            return;
        }

        root.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "AddProvider takes ownership of the provider; the logger factory disposes it when the host is disposed. The rule cannot see ownership transfer through a method argument.")]
    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();

        // Before Core's registrations, which use TryAdd: the buffer the loggers write into must be
        // the one the Status and Logs pages read.
        builder.Services.AddSingleton(_logBuffer);

        // A singleton because the Plugins page builds a new view model on every visit, and what
        // this records — plugins installed and removed since startup — has to last as long as the
        // process.
        builder.Services.AddSingleton<PluginSession>();

        builder.Services.AddDongledCore();

        AddUpdates(builder.Services);

        builder.Logging.ClearProviders();

        // Trace at the factory, with each provider filtered separately, so the Logs page can show
        // more than the file keeps.
        builder.Logging.SetMinimumLevel(LogLevel.Trace);

        builder.Logging.AddProvider(new RingBufferLoggerProvider(_logBuffer, TimeProvider.System));
        builder.Logging.AddProvider(new FileLoggerProvider(StoragePaths.LogDirectory, TimeProvider.System));

        // Debug in memory, so lowering the Logs page's filter shows history; Information on disk,
        // so a day's file stays readable.
        builder.Logging.AddFilter<RingBufferLoggerProvider>(category: null, level: LogLevel.Debug);
        builder.Logging.AddFilter<FileLoggerProvider>(category: null, level: LogLevel.Information);

        return builder.Build();
    }

    /// <summary>Register the update checker and installer.</summary>
    /// <remarks>
    /// Here rather than in Core because what they need to know about the build is stamped on this
    /// assembly. A build from source reads as not official, and then nothing here makes a request.
    /// </remarks>
    private static void AddUpdates(IServiceCollection services)
    {
        var build = BuildInfo.FromAssembly(typeof(App).Assembly);
        services.AddSingleton(build);

        // Disposed with the container. Redirects are followed, which a release download needs, and
        // the handler never follows one from HTTPS down to HTTP.
        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });

        services.AddSingleton(provider => new GitHubReleaseClient(
            provider.GetRequiredService<HttpClient>(),
            build.DisplayVersion));

        services.AddSingleton(provider => new UpdateInstaller(
            provider.GetRequiredService<GitHubReleaseClient>(),
            build,
            AppContext.BaseDirectory,
            StoragePaths.StagingDirectory,
            Path.GetFileName(Environment.ProcessPath) is { Length: > 0 } exe ? exe : "Dongled.exe",
            provider.GetRequiredService<ILogger<UpdateInstaller>>()));

        services.AddSingleton<IUpdateService>(provider => new UpdateService(
            build,
            provider.GetRequiredService<GitHubReleaseClient>(),
            provider.GetRequiredService<UpdateInstaller>(),
            provider.GetRequiredService<IConfigStore>(),
            provider.GetRequiredService<IUpdateStateStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<UpdateService>>()));
    }

    /// <summary>Delete what the last update moved aside, retrying while the replaced copy finishes exiting.</summary>
    private static async Task CleanUpAfterUpdateAsync(ILogger logger)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (AppFolderSwap.CleanUp(AppContext.BaseDirectory, logger))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        logger.LogInformation("Some files the last update replaced are still in use; they will be removed at the next start.");
    }

    private void RequestExit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;

        // Not awaited, because a menu handler cannot await; the shutdown still has to finish
        // before the process ends, or a provider is killed mid-stop.
        _ = ShutDownAndExitAsync();
    }

    private async Task ShutDownAndExitAsync()
    {
        try
        {
            await _shutdown.CancelAsync();

            if (_pluginHost is not null)
            {
                await _pluginHost;
            }

            // Asynchronously, because PluginHost is only IAsyncDisposable: disposing the container
            // synchronously throws.
            if (_host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                _host?.Dispose();
            }
        }
        catch (Exception)
        {
            // The app is exiting and the log providers are among the things being torn down.
            // Exiting untidily is better than refusing to exit.
        }
        finally
        {
            _shutdown.Dispose();

            if (_restarting)
            {
                StartReplacement();
            }

            _window?.PrepareForExit();
            Exit();
        }
    }
}
