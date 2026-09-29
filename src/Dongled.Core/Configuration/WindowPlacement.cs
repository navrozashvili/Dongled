namespace Dongled.Core.Configuration;

/// <summary>
/// Where the window was the last time it was put away. Written only by the UI.
/// </summary>
/// <remarks>
/// <para>
/// Its own file rather than a section of <see cref="AppConfig"/>, for two reasons. The first is that
/// this is remembered rather than configured: nothing here is a choice the user made, and a
/// <c>window</c> object in the middle of a file whose whole point is hand-editable rules and
/// settings invites edits to something no one should have to think about. The second is concrete —
/// the settings view model loads one <see cref="AppConfig"/> when the page is navigated to and saves
/// that whole object on every toggle, so a placement written while the page was open would be
/// silently reverted by the next theme change.
/// </para>
/// <para>
/// Every measurement is in physical screen pixels, because that is the unit
/// <c>Microsoft.UI.Windowing.AppWindow</c> reads and writes. Storing device-independent units would
/// mean recording the scale factor beside them and reasoning about the case where the window is
/// restored onto a display with a different one; screen coordinates restore a window to exactly
/// where it was with no arithmetic at all, and the case that actually needs handling — the display
/// being gone — needs handling either way.
/// </para>
/// </remarks>
public sealed class WindowPlacement
{
    /// <summary>Schema version of this file. Bumped only for a breaking change.</summary>
    /// <remarks>
    /// Read like the other two files: a file declaring a version this build does not understand is
    /// treated as unreadable, and is left on disk rather than overwritten.
    /// </remarks>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Left edge, in physical screen pixels.</summary>
    public int X { get; set; }

    /// <summary>Top edge, in physical screen pixels.</summary>
    public int Y { get; set; }

    /// <summary>Width including the frame, in physical pixels.</summary>
    public int Width { get; set; }

    /// <summary>Height including the frame, in physical pixels.</summary>
    public int Height { get; set; }

    /// <summary>
    /// Whether the window was maximized. The other four values are then the size it would return to,
    /// not the size it filled, so unmaximizing after a restart lands where the user left it.
    /// </summary>
    public bool Maximized { get; set; }
}
