using System.Drawing;
using CommunityToolkit.Mvvm.ComponentModel;
using Dongled.Abstractions;
using Dongled.App.Presentation;
using Dongled.App.Services;
using Dongled.App.Tray;
using Dongled.Core.Audio;
using Dongled.Core.Configuration;
using Dongled.Core.Engine;

namespace Dongled.App.ViewModels;

/// <summary>
/// What the window frame itself shows: the tray tooltip, which names the current default device,
/// and the tray icon, which shows the leading battery level.
/// </summary>
internal sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Windows truncates a notification-area tooltip at 127 characters, so it is truncated here
    /// where the ellipsis can be put somewhere sensible.
    /// </summary>
    private const int ToolTipLimit = 127;

    /// <summary>
    /// How often the tray battery figure is refreshed. Far slower than the Status page's two
    /// seconds: nobody watches a percentage tick down, and the devices reporting one update on the
    /// order of minutes. The renderer caches, so the cost is a dictionary lookup either way.
    /// </summary>
    private static readonly TimeSpan BatteryPollInterval = TimeSpan.FromSeconds(30);

    private readonly IAudioEndpointService _endpoints;
    private readonly IUiDispatcher _dispatcher;
    private readonly ISwitchingEngine _engine;
    private readonly IConfigStore _configStore;
    private readonly ITrayIconRenderer _renderer;
    private readonly IUiTimer _batteryTimer;

    private int _iconSize = 16;
    private bool _disposed;

    /// <param name="endpoints">Windows audio.</param>
    /// <param name="engine">The engine, for battery readings.</param>
    /// <param name="configStore">Configuration, for the user's battery order.</param>
    /// <param name="renderer">Draws the icon. Owned by this view model and disposed with it.</param>
    /// <param name="dispatcher">The UI thread's queue.</param>
    public ShellViewModel(
        IAudioEndpointService endpoints,
        ISwitchingEngine engine,
        IConfigStore configStore,
        ITrayIconRenderer renderer,
        IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _endpoints = endpoints;
        _engine = engine;
        _configStore = configStore;
        _renderer = renderer;
        _dispatcher = dispatcher;

        _endpoints.DefaultChanged += OnDefaultChanged;

        _batteryTimer = _dispatcher.CreateTimer(BatteryPollInterval, isRepeating: true, RefreshTrayIcon);
        _batteryTimer.Start();

        Refresh();
    }

    /// <summary>Tooltip for the notification-area icon.</summary>
    [ObservableProperty]
    public partial string TrayToolTip { get; private set; } = "Dongled";

    /// <summary>
    /// The icon the notification area should show. Owned by the renderer, which hands the same
    /// instance back whenever a state recurs: a reader must never dispose it, and a control that
    /// insists on owning what it is given must be handed a copy.
    /// </summary>
    [ObservableProperty]
    public partial Icon? TrayIcon { get; private set; }

    /// <summary>Re-read the current default and rebuild the tooltip.</summary>
    public void Refresh()
    {
        string media;

        try
        {
            var endpoints = _endpoints.Enumerate();
            media = DisplayNames.ForDefault(endpoints, _endpoints.GetDefaults().MultimediaId);
        }
        catch (Exception)
        {
            // Broad by the seam's own documentation: a transient COM failure must not be read as
            // "there are no endpoints", and a tooltip is not worth a crash.
            media = "unavailable";
        }

        var text = $"Dongled — playing to {media}";

        TrayToolTip = text.Length > ToolTipLimit
            ? string.Concat(text.AsSpan(0, ToolTipLimit - 1), "…")
            : text;

        RefreshTrayIcon();
    }

    /// <summary>
    /// Tell the view model what size the notification area wants, and redraw if it changed.
    /// </summary>
    /// <param name="size">Square edge in pixels, from <c>GetSystemMetrics(SM_CXSMICON)</c>.</param>
    public void SetIconSize(int size)
    {
        if (size <= 0 || _iconSize == size)
        {
            return;
        }

        _iconSize = size;
        RefreshTrayIcon();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _batteryTimer.Dispose();
        _renderer.Dispose();
        _endpoints.DefaultChanged -= OnDefaultChanged;

        // The renderer just destroyed whatever this pointed at. Nothing reads it after dispose
        // today — the PropertyChanged subscription is already gone by the time this runs — but
        // nulling it makes that invariant self-evident rather than relying on nobody looking.
        TrayIcon = null;
    }

    // Raised on a thread Windows owns, per IAudioEndpointService's own remarks, so it is marshalled
    // rather than assumed to be on the UI thread.
    private void OnDefaultChanged(object? sender, DefaultChangedEventArgs e) =>
        _dispatcher.TryEnqueue(Refresh);

    private void RefreshTrayIcon()
    {
        // OnDefaultChanged marshals through TryEnqueue, and unsubscribing cannot recall a
        // callback already sitting in the dispatcher queue: one can still be pumped after
        // Dispose() has torn down the renderer. Without this guard that reaches Render below on
        // a disposed renderer, which throws ObjectDisposedException by design.
        if (_disposed)
        {
            return;
        }

        try
        {
            var preferred = _configStore.Load().App.BatteryDisplayOrder;
            var pick = BatteryOrdering.PickForTray(BatteryOrdering.Order(_engine.SourceStates(), preferred));

            var content = pick?.Battery is { } reading
                ? new TrayIconContent(reading.Percent, reading.Charge)
                : new TrayIconContent(null, ChargeState.Unknown);

            // Render is part of this try, not just the configuration read above: with a
            // percentage to draw it walks into BatteryTrayIconRenderer's GDI+ path (new Bitmap,
            // Graphics.FromImage, GetHicon()), which can throw under handle or memory pressure.
            // This runs on a 30-second timer for the life of the app, so a rendering failure must
            // not go unhandled any more than a configuration read failure may.
            TrayIcon = _renderer.Render(content, _iconSize);
        }
        catch (Exception)
        {
            // An icon is decoration; the plain application icon is a perfectly good fallback for
            // either failure above. Two things have to hold for a fallback to be worth anything,
            // because a throw from here escapes a 30-second timer tick and ends the process.
            //
            // The render itself is safe even when the failure came from drawing: content with no
            // percentage never reaches CreateIcon's GDI+ path — the renderer's own contract says
            // that case returns the already-built base icon directly, ahead of the cache and any
            // drawing.
            //
            // The assignment is safe too, which is less obvious: setting this property runs
            // PropertyChanged synchronously, and TrayIconBinding pushes the result into the tray
            // control. That control disposes whatever it is handed once something displaces it,
            // so it is handed a clone and never the renderer's own instance — see
            // TrayIconBinding.ForControl.
            TrayIcon = _renderer.Render(new TrayIconContent(null, ChargeState.Unknown), _iconSize);
        }
    }
}
