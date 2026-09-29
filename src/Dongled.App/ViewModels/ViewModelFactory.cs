using Dongled.App.Presentation;
using Dongled.App.Services;
using Dongled.App.Startup;
using Dongled.Core.Audio;
using Dongled.Core.Configuration;
using Dongled.Core.Engine;
using Dongled.Core.Logging;
using Dongled.Core.Updates;

namespace Dongled.App.ViewModels;

/// <summary>Builds each page's view model from the app's services.</summary>
/// <remarks>
/// Pages are constructed by the XAML runtime, so they cannot take constructor arguments. Each page
/// is handed this instead and asks it for a fresh view model every time it is navigated to.
/// </remarks>
internal sealed class ViewModelFactory
{
    private readonly IConfigStore _configStore;
    private readonly IAudioEndpointService _endpoints;
    private readonly ISwitchingEngine _engine;
    private readonly IPluginRuntime _plugins;
    private readonly IPluginInstaller _installer;
    private readonly PluginSession _pluginSession;
    private readonly TimeProvider _time;
    private readonly LogRingBuffer _log;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action _requestRestart;
    private readonly Action<AppTheme> _applyTheme;
    private readonly Action _notifyTrayOrderChanged;
    private readonly IUpdateService _updates;
    private readonly IStartupRegistration _startup;

    /// <param name="configStore">Configuration.</param>
    /// <param name="endpoints">Windows audio.</param>
    /// <param name="engine">The switching engine.</param>
    /// <param name="plugins">The running plugin host.</param>
    /// <param name="installer">Installs and removes plugin packages.</param>
    /// <param name="pluginSession">What has been installed and removed since startup.</param>
    /// <param name="time">The clock.</param>
    /// <param name="log">The in-memory log.</param>
    /// <param name="dispatcher">The UI thread.</param>
    /// <param name="requestRestart">Shuts the app down and starts a fresh copy.</param>
    /// <param name="applyTheme">Applies a theme to the live window.</param>
    /// <param name="notifyTrayOrderChanged">Redraws the tray icon after the battery order changes.</param>
    /// <param name="updates">Checks for and installs updates.</param>
    /// <param name="startup">The start-with-Windows entry.</param>
    public ViewModelFactory(
        IConfigStore configStore,
        IAudioEndpointService endpoints,
        ISwitchingEngine engine,
        IPluginRuntime plugins,
        IPluginInstaller installer,
        PluginSession pluginSession,
        TimeProvider time,
        LogRingBuffer log,
        IUiDispatcher dispatcher,
        Action requestRestart,
        Action<AppTheme> applyTheme,
        Action notifyTrayOrderChanged,
        IUpdateService updates,
        IStartupRegistration startup)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(pluginSession);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(requestRestart);
        ArgumentNullException.ThrowIfNull(applyTheme);
        ArgumentNullException.ThrowIfNull(notifyTrayOrderChanged);
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(startup);

        _configStore = configStore;
        _endpoints = endpoints;
        _engine = engine;
        _plugins = plugins;
        _installer = installer;
        _pluginSession = pluginSession;
        _time = time;
        _log = log;
        _dispatcher = dispatcher;
        _requestRestart = requestRestart;
        _applyTheme = applyTheme;
        _notifyTrayOrderChanged = notifyTrayOrderChanged;
        _updates = updates;
        _startup = startup;
    }

    /// <param name="dialogs">Dialogs shown over the page asking for the view model.</param>
    public StatusViewModel CreateStatus(IDialogService dialogs) =>
        new(_endpoints, _engine, _configStore, _log, _dispatcher, _updates, CreateUpdateFlow(dialogs));

    public RulesViewModel CreateRules() =>
        new(_configStore, _endpoints, _engine, _dispatcher);

    /// <param name="dialogs">Dialogs shown over the page asking for the view model.</param>
    public PluginsViewModel CreatePlugins(IDialogService dialogs) =>
        new(_plugins, _configStore, _engine, _time, _installer, _pluginSession, _dispatcher, dialogs, _requestRestart);

    public BatteryViewModel CreateBattery() =>
        new(_engine, _configStore, _dispatcher, _notifyTrayOrderChanged);

    public LogsViewModel CreateLogs() =>
        new(_log, _dispatcher);

    /// <param name="dialogs">Dialogs shown over the page asking for the view model.</param>
    public SettingsViewModel CreateSettings(IDialogService dialogs) =>
        new(_configStore, _engine, _plugins, _applyTheme, _updates, CreateUpdateFlow(dialogs), _dispatcher, _startup);

    private UpdateFlow CreateUpdateFlow(IDialogService dialogs) =>
        new(_updates, dialogs, _requestRestart);
}
