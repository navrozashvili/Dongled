using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dongled.App.Presentation;
using Dongled.App.Services;
using Dongled.App.Startup;
using Dongled.Core.Configuration;
using Dongled.Core.Engine;
using Dongled.Core.Updates;

namespace Dongled.App.ViewModels;

/// <summary>
/// The Settings page: start with Windows, apply rules at startup, theme, where the files live,
/// and the version.
/// </summary>
/// <remarks>
/// <para>
/// Starting with Windows is per user only; see <see cref="StartupRegistration"/> for why there is no
/// machine-wide option.
/// </para>
/// <para>
/// Every change persists immediately. A Save button would let the user change something, navigate
/// away, and lose it.
/// </para>
/// </remarks>
internal sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly IConfigStore _configStore;
    private readonly ISwitchingEngine _engine;
    private readonly IPluginRuntime _plugins;
    private readonly Action<AppTheme> _applyTheme;
    private readonly IUpdateService _updates;
    private readonly UpdateFlow _updateFlow;
    private readonly IUiDispatcher _dispatcher;
    private readonly AppConfig _config;

    private bool _startWithWindows;
    private bool _startMinimized;
    private bool _applyRulesOnStartup;
    private bool _checkForUpdates;
    private int _themeIndex;
    private double? _downloadProgress;
    private bool _disposed;

    /// <summary>Set while the initial values are assigned, so loading does not trigger a save.</summary>
    private readonly bool _loaded;

    /// <param name="configStore">Where settings are read and written.</param>
    /// <param name="engine">Told to re-read configuration after a save.</param>
    /// <param name="plugins">Told to re-read per-plugin log levels after a save.</param>
    /// <param name="applyTheme">Applies a theme to the live window.</param>
    /// <param name="updates">Checks for and installs updates.</param>
    /// <param name="updateFlow">What the Update button runs.</param>
    /// <param name="dispatcher">The UI thread, which update changes are marshalled to.</param>
    public SettingsViewModel(
        IConfigStore configStore,
        ISwitchingEngine engine,
        IPluginRuntime plugins,
        Action<AppTheme> applyTheme,
        IUpdateService updates,
        UpdateFlow updateFlow,
        IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(applyTheme);
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(updateFlow);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _configStore = configStore;
        _engine = engine;
        _plugins = plugins;
        _applyTheme = applyTheme;
        _updates = updates;
        _updateFlow = updateFlow;
        _dispatcher = dispatcher;

        _config = configStore.Load();

        _startMinimized = _config.App.StartMinimized;
        _applyRulesOnStartup = _config.App.ApplyRulesOnStartup;
        _checkForUpdates = _config.App.CheckForUpdates;
        _themeIndex = (int)_config.App.Theme;

        _updates.Changed += OnUpdatesChanged;
        RefreshUpdate();

        // Read from the registry rather than from configuration. The two can disagree — a user can
        // delete the Run value by hand — and the registry is the one that decides what actually
        // happens at logon, so it is the one shown.
        _startWithWindows = StartupRegistration.IsEnabled();


        _loaded = true;
    }

    /// <summary>Whether to start when this user logs in. Per user; never machine wide.</summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!SetProperty(ref _startWithWindows, value) || !_loaded)
            {
                return;
            }

            try
            {
                StartupRegistration.SetEnabled(value);
                Problem = string.Empty;
            }
            catch (Exception ex)
            {
                // Reverted so the checkbox reflects what is actually in the registry rather than what
                // the user asked for and did not get.
                _startWithWindows = !value;
                OnPropertyChanged();
                Problem = $"Start with Windows could not be changed: {ex.Message}";
                return;
            }

            // Kept in configuration too, so the value survives a profile that roams to a machine
            // where the Run key was never written. The registry stays the authority on this machine.
            _config.App.StartWithWindows = value;
            Persist();
        }
    }

    /// <summary>
    /// Whether to start with no window, showing only the notification-area icon.
    /// </summary>
    /// <remarks>
    /// Independent of <see cref="StartWithWindows"/> on purpose, even though starting at logon is
    /// the reason to want it. The two answer different questions — whether the app starts, and
    /// whether starting puts a window on screen — and tying them together would mean a user who
    /// launches from the Start menu could not have the second without the first.
    /// </remarks>
    public bool StartMinimized
    {
        get => _startMinimized;
        set
        {
            if (!SetProperty(ref _startMinimized, value) || !_loaded)
            {
                return;
            }

            _config.App.StartMinimized = value;
            Persist();
        }
    }

    /// <summary>Whether to reconcile rules against current device state at startup.</summary>
    public bool ApplyRulesOnStartup
    {
        get => _applyRulesOnStartup;
        set
        {
            if (!SetProperty(ref _applyRulesOnStartup, value) || !_loaded)
            {
                return;
            }

            _config.App.ApplyRulesOnStartup = value;
            Persist();
        }
    }

    /// <summary>Whether to ask GitHub for a newer release when the window is opened.</summary>
    public bool CheckForUpdates
    {
        get => _checkForUpdates;
        set
        {
            if (!SetProperty(ref _checkForUpdates, value) || !_loaded)
            {
                return;
            }

            _config.App.CheckForUpdates = value;
            Persist();
            RefreshUpdate();
        }
    }

    /// <summary>
    /// Whether this build checks for updates at all. False for a build from source, where the page
    /// hides the setting and the button rather than offering something that does nothing.
    /// </summary>
    public bool IsUpdateSupported => _updates.IsSupported;

    /// <summary>The opposite of <see cref="IsUpdateSupported"/>, for the note a build from source shows instead.</summary>
    public bool IsDevelopmentBuild => !_updates.IsSupported;

    /// <summary>The update line: up to date, available, checking, or why the last check failed.</summary>
    [ObservableProperty]
    public partial string UpdateStatus { get; private set; } = string.Empty;

    /// <summary>The offered release's page, or null when nothing is offered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReleaseNotes))]
    public partial Uri? ReleaseNotesUrl { get; private set; }

    /// <summary>Whether there is a release page to link to.</summary>
    public bool HasReleaseNotes => ReleaseNotesUrl is not null;

    /// <summary>Whether a check can be started now.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    public partial bool CanCheckNow { get; private set; }

    /// <summary>Whether the Update button shows and can be pressed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    public partial bool CanUpdate { get; private set; }

    /// <summary>Selected theme, as an index into System, Light, Dark.</summary>
    public int ThemeIndex
    {
        get => _themeIndex;
        set
        {
            if (!SetProperty(ref _themeIndex, value) || !_loaded)
            {
                return;
            }

            // A ComboBox reports -1 when its selection is cleared, which is not a theme.
            var theme = Enum.IsDefined((AppTheme)value) ? (AppTheme)value : AppTheme.System;

            _config.App.Theme = theme;
            _applyTheme(theme);
            Persist();
        }
    }

    /// <summary>Where configuration, state and logs live.</summary>
    public static string ConfigFolder => StoragePaths.AppDataDirectory;

    /// <summary>
    /// Whether the writable paths have been redirected away from the real per-user folder.
    /// </summary>
    public static bool IsRedirected => StoragePaths.IsRedirected;

    /// <summary>What the page says when the paths are redirected.</summary>
    public static string RedirectionNotice =>
        $"{StoragePaths.DataDirectoryVariable} is set, so this session reads and writes {StoragePaths.AppDataDirectory} instead of the normal per-user folder. Nothing here affects a configuration held anywhere else.";

    /// <summary>The running build's version.</summary>
    public static string Version
    {
        get
        {
            var informational = typeof(SettingsViewModel).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (string.IsNullOrWhiteSpace(informational))
            {
                return typeof(SettingsViewModel).Assembly.GetName().Version?.ToString() ?? "unknown";
            }

            // The source-revision suffix a deterministic build appends is noise on a settings page.
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }
    }

    /// <summary>Set when something could not be saved, so the page can say so.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string Problem { get; private set; } = string.Empty;

    /// <summary>Whether <see cref="Problem"/> has anything to say.</summary>
    public bool HasProblem => Problem.Length > 0;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _updates.Changed -= OnUpdatesChanged;
    }

    /// <summary>Ask GitHub now, whatever the setting and however recently it was asked.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private async Task CheckNowAsync()
    {
        try
        {
            await _updates.CheckNowAsync();
        }
        catch (OperationCanceledException)
        {
        }

        RefreshUpdate();
    }

    /// <summary>Download, verify and install the offered release, after asking.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateAsync()
    {
        _downloadProgress = null;

        await _updateFlow.RunAsync(new UiProgress<double>(_dispatcher, fraction =>
        {
            _downloadProgress = fraction;
            RefreshUpdate();
        }));

        _downloadProgress = null;
        RefreshUpdate();
    }

    private void RefreshUpdate()
    {
        if (_disposed || !_updates.IsSupported)
        {
            return;
        }

        var snapshot = _updates.Current;
        var busy = snapshot.IsInstalling || snapshot.Status == UpdateCheckStatus.Checking;

        UpdateStatus = UpdateText.Status(snapshot, _checkForUpdates, _downloadProgress);
        ReleaseNotesUrl = snapshot.ReleaseNotesUrl;
        CanCheckNow = !busy;
        CanUpdate = snapshot.AvailableVersion is not null && !busy;
    }

    private void OnUpdatesChanged(object? sender, EventArgs e) =>
        _dispatcher.TryEnqueue(RefreshUpdate);

    /// <summary>Opens the folder holding configuration, state and logs.</summary>
    [RelayCommand]
    private void OpenConfigFolder()
    {
        try
        {
            Directory.CreateDirectory(StoragePaths.AppDataDirectory);

            // UseShellExecute is required: without it this tries to execute the directory.
            using var process = Process.Start(new ProcessStartInfo(StoragePaths.AppDataDirectory)
            {
                UseShellExecute = true,
            });

            Problem = string.Empty;
        }
        catch (Exception ex)
        {
            Problem = $"The folder could not be opened: {ex.Message}";
        }
    }

    private void Persist()
    {
        try
        {
            _configStore.Save(_config);
            Problem = string.Empty;
        }
        catch (Exception ex)
        {
            // Save is documented as deliberately not failing soft, precisely so this can be shown
            // rather than leaving the user believing a setting was stored.
            Problem = $"Settings could not be saved: {ex.Message}";
            return;
        }

        // Both, together, after every save. The engine re-reads rules and settings; the host re-reads
        // per-plugin log levels. Neither notices a file change on its own.
        _engine.RequestConfigurationReload();
        _plugins.ReloadConfiguration();
    }
}
