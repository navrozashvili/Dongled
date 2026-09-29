using System.Collections.Generic;
using Dongled.App.Interop;
using Dongled.App.Presentation;
using Dongled.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Dongled.App.Windowing;

/// <summary>
/// Puts the window where it was last left, keeps track of where the user moves it, and writes that
/// back for the next launch.
/// </summary>
internal sealed class WindowPlacementTracker
{
    private readonly AppWindow _window;
    private readonly nint _handle;
    private readonly IWindowPlacementStore _store;
    private readonly ILogger _logger;

    /// <summary>
    /// Where the window sits when it is neither maximized nor minimized, which is the only shape
    /// worth remembering.
    /// </summary>
    /// <remarks>
    /// Tracked as the window moves rather than read when saving, because by then it may be
    /// maximized, and a maximized window reports the size it fills rather than the size it returns
    /// to.
    /// </remarks>
    private WindowBounds _placement;

    /// <summary>Whether the window has moved or been resized since the placement was applied.</summary>
    private bool _changed;

    /// <summary>
    /// Whether the window was maximized when last put away and has not been shown since.
    /// </summary>
    /// <remarks>
    /// Held rather than acted on, because <see cref="OverlappedPresenter.Maximize"/> also shows the
    /// window, which must not happen before the theme is applied or at all for a launch that starts
    /// in the notification area.
    /// </remarks>
    private bool _maximizeOnFirstShow;

    /// <param name="window">The window to place.</param>
    /// <param name="handle">Its HWND, for reading the display scale.</param>
    /// <param name="store">Where the placement is remembered.</param>
    /// <param name="logger">Where a placement that could not be applied is reported.</param>
    public WindowPlacementTracker(AppWindow window, nint handle, IWindowPlacementStore store, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _window = window;
        _handle = handle;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Size and position the window: where it was last left, or a default centred on the display
    /// it opens on. Call before the window is first shown, so it never jumps.
    /// </summary>
    /// <remarks>
    /// Without this the window takes whatever size Windows chooses, which on a high-resolution
    /// display is most of the screen, and the pages cap their content width so the extra arrives as
    /// empty margin.
    /// </remarks>
    public void Apply()
    {
        var scale = User32.ScaleFor(_handle);

        // Primary rather than Nearest: the window has no meaningful position yet.
        var opening = DisplayArea.GetFromWindowId(_window.Id, DisplayAreaFallback.Primary);

        WindowPlacement? saved = null;
        WindowBounds? restored = null;

        try
        {
            saved = _store.Load();

            if (saved is not null)
            {
                restored = WindowGeometry.Restore(
                    new WindowBounds(saved.X, saved.Y, saved.Width, saved.Height),
                    WorkAreas(),
                    scale);
            }
        }
        catch (Exception ex)
        {
            // Where the window was last time is a convenience and must never stop it opening. The
            // centred default is always available.
            _logger.LogWarning(ex, "The remembered window position could not be applied. Opening at the default size instead.");
            restored = null;
        }

        _placement = restored ?? WindowGeometry.Default(ToBounds(opening.WorkArea), scale);

        _window.MoveAndResize(new RectInt32(_placement.X, _placement.Y, _placement.Width, _placement.Height));

        // In physical pixels: the presenter's minimum is in screen coordinates and is not scaled.
        if (_window.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = WindowGeometry.Scale(WindowGeometry.MinimumWidth, scale);
            presenter.PreferredMinimumHeight = WindowGeometry.Scale(WindowGeometry.MinimumHeight, scale);
        }

        // Only when the remembered placement was usable: maximizing over a discarded one would hide
        // that it had been discarded.
        _maximizeOnFirstShow = restored is not null && saved is { Maximized: true };

        _window.Changed += OnChanged;
    }

    /// <summary>Show the window, maximizing it first if that is how it was last left.</summary>
    public void Show()
    {
        if (_maximizeOnFirstShow)
        {
            _maximizeOnFirstShow = false;

            // Maximize shows the window too, but does not raise one that was hidden, so the Show
            // below is still needed.
            (_window.Presenter as OverlappedPresenter)?.Maximize();
        }

        _window.Show();
    }

    /// <summary>Record where the window is, so the next launch can put it back.</summary>
    /// <remarks>
    /// Does nothing until the window has been moved or resized, so a session where the user never
    /// touched it does not rewrite the file with the values it just read.
    /// </remarks>
    public void Save()
    {
        if (!_changed)
        {
            return;
        }

        _store.Save(new WindowPlacement
        {
            X = _placement.X,
            Y = _placement.Y,
            Width = _placement.Width,
            Height = _placement.Height,
            Maximized = _window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized },
        });

        _changed = false;
    }

    /// <summary>Stop following the window.</summary>
    public void StopTracking() => _window.Changed -= OnChanged;

    private static WindowBounds ToBounds(RectInt32 rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>The work area of every attached display.</summary>
    /// <remarks>
    /// Indexed rather than enumerated, and it must stay that way: the projection of
    /// <see cref="DisplayArea.FindAll"/> cannot produce an enumerator, and every <c>foreach</c> or
    /// LINQ operator over it throws <see cref="InvalidCastException"/>. <c>Count</c> and the
    /// indexer work.
    /// </remarks>
    private static List<WindowBounds> WorkAreas()
    {
        var displays = DisplayArea.FindAll();
        var areas = new List<WindowBounds>(displays.Count);

        for (var index = 0; index < displays.Count; index++)
        {
            areas.Add(ToBounds(displays[index].WorkArea));
        }

        return areas;
    }

    private void OnChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange && !args.DidPresenterChange)
        {
            return;
        }

        _changed = true;

        // Maximized and minimized bounds are no use to restore from. Only the restored shape is
        // kept; whether the window is maximized is read when saving.
        if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            _placement = new WindowBounds(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height);
        }
    }
}
