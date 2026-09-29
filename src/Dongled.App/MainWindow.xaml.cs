using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Dongled.App.Pages;
using Dongled.App.Tray;
using Dongled.App.ViewModels;
using Dongled.App.Windowing;
using Dongled.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Dongled.App;

/// <summary>
/// The one window: a <see cref="NavigationView"/> over the five pages, plus the notification-area
/// icon.
/// </summary>
/// <remarks>
/// Closing hides rather than exits. A tray app that stopped switching when its window was closed
/// would silently stop applying rules. The tray menu's Exit item is the only way to end the
/// process.
/// </remarks>
internal sealed partial class MainWindow : Window
{
    private readonly ViewModelFactory _viewModels;
    private readonly WindowPlacementTracker _placement;
    private readonly TrayIconBinding _trayIcon;
    private readonly nint _handle;

    private bool _exiting;

    /// <param name="shell">What the frame and tray show. Owned by this window and disposed on exit.</param>
    /// <param name="viewModels">Builds each page's view model.</param>
    /// <param name="placementStore">Where the window's position is remembered.</param>
    /// <param name="logger">Where problems applying that position are reported.</param>
    /// <param name="requestExit">Starts an orderly shutdown; see <see cref="App"/>.</param>
    internal MainWindow(
        ShellViewModel shell,
        ViewModelFactory viewModels,
        IWindowPlacementStore placementStore,
        ILogger<MainWindow> logger,
        Action requestExit)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(viewModels);
        ArgumentNullException.ThrowIfNull(placementStore);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(requestExit);

        _viewModels = viewModels;

        // Assigned before InitializeComponent, because x:Bind reads the two commands only once.
        Shell = shell;
        ShowWindowCommand = new RelayCommand(ShowFromTray);
        ExitCommand = new RelayCommand(requestExit);

        InitializeComponent();

        // Evaluate every x:Bind now rather than on first activation. For a Window, the generated
        // bindings are initialized only by the Activated event, so a launch that starts in the
        // notification area would leave the tray icon with no commands and no tooltip, and the
        // user with no way to open or exit the app.
        Bindings.Update();

        Shell.SetIconSize(NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON));
        _trayIcon = new TrayIconBinding(TrayIcon, Shell);

        // Without a system backdrop the NavigationView pane renders solid black in the light theme.
        // Mica where it exists; acrylic on Windows 10.
        SystemBackdrop = MicaController.IsSupported() ? new MicaBackdrop() : new DesktopAcrylicBackdrop();

        // Draw into the caption area so the title bar follows the app's theme.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        SetWindowIcon();

        _handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _placement = new WindowPlacementTracker(AppWindow, _handle, placementStore, logger);
        _placement.Apply();

        AppWindow.Closing += OnClosing;

        // Re-tinted whenever the effective theme changes, whether the user picked one on the
        // Settings page or Windows switched under "Follow Windows".
        if (Content is FrameworkElement root)
        {
            root.ActualThemeChanged += (_, _) => ApplyCaptionTheme();
        }

        ApplyCaptionTheme();

        Navigate("Status");
    }

    /// <summary>What the window frame binds: the tray tooltip.</summary>
    internal ShellViewModel Shell { get; }

    /// <summary>Brings the window back from the tray.</summary>
    public ICommand ShowWindowCommand { get; }

    /// <summary>Ends the app. The only path that does.</summary>
    public ICommand ExitCommand { get; }

    /// <summary>
    /// Put the notification-area icon up without showing the window, for a launch that starts
    /// hidden.
    /// </summary>
    /// <returns>
    /// Whether the icon was created. If not, the caller must show the window: a process with
    /// neither a window nor a tray icon cannot be seen or stopped.
    /// </returns>
    /// <remarks>
    /// The icon would normally be created when the window is first activated, which a hidden launch
    /// never does. Efficiency mode is declined because the app keeps polling devices while hidden,
    /// and having Windows treat it as background work would delay the battery readings the icon
    /// shows.
    /// </remarks>
    public bool TryStartHidden()
    {
        try
        {
            TrayIcon.ForceCreate(enablesEfficiencyMode: false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Show and focus the window, whether hidden, never shown, or merely behind something.</summary>
    /// <remarks>
    /// Every path that puts the window on screen comes through here, because this is where a
    /// remembered maximized state is applied.
    /// </remarks>
    public void ShowFromTray()
    {
        _placement.Show();
        Activate();

        Opened?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised each time <see cref="ShowFromTray"/> puts the window on screen, after it is shown.
    /// </summary>
    /// <remarks>
    /// The one moment the user is known to be looking at the app, and so the only trigger for an
    /// update check. A launch that starts in the notification area does not raise it.
    /// </remarks>
    public event EventHandler? Opened;

    /// <summary>Stop hiding on close, because the next close is the real one.</summary>
    /// <remarks>
    /// Called by <see cref="App"/> once shutdown has finished. Saves the placement too, because a
    /// window that is already hidden is never closed again.
    /// </remarks>
    public void PrepareForExit()
    {
        _exiting = true;

        _placement.Save();
        _placement.StopTracking();

        _trayIcon.Detach();
        Shell.Dispose();
    }

    /// <summary>Tint the caption buttons to match the effective theme.</summary>
    internal void ApplyCaptionTheme()
    {
        if (Content is FrameworkElement root)
        {
            CaptionButtons.Apply(AppWindow.TitleBar, root.ActualTheme == ElementTheme.Dark);
        }
    }

    /// <summary>Give the window the icon the taskbar and Alt+Tab show.</summary>
    /// <remarks>
    /// The <c>ApplicationIcon</c> build property covers the executable; this covers the running
    /// window. An icon is decoration, so a missing file is ignored.
    /// </remarks>
    private void SetWindowIcon()
    {
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIcon.ico");

            if (File.Exists(icon))
            {
                AppWindow.SetIcon(icon);
            }
        }
        catch (Exception)
        {
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting)
        {
            return;
        }

        args.Cancel = true;

        // Saved on every hide rather than only at exit, so a session ended by a crash or by Windows
        // shutting down still keeps the arrangement.
        _placement.Save();

        AppWindow.Hide();
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            Navigate(tag);
        }
    }

    private void Navigate(string tag)
    {
        var page = tag switch
        {
            "Rules" => typeof(RulesPage),
            "Plugins" => typeof(PluginsPage),
            "Battery" => typeof(BatteryPage),
            "Logs" => typeof(LogsPage),
            "Settings" => typeof(SettingsPage),
            _ => typeof(StatusPage),
        };

        if (ContentFrame.CurrentSourcePageType == page)
        {
            return;
        }

        ContentFrame.Navigate(page, new PageContext(_viewModels, _handle));
    }
}
