using System.Collections.Generic;
using System.Linq;

namespace Dongled.App.Presentation;

/// <summary>A rectangle in physical screen pixels.</summary>
/// <remarks>
/// Deliberately not <c>Windows.Graphics.RectInt32</c>, even though that is what the windowing API
/// takes and what this converts to at the call site. Keeping the arithmetic on a plain struct is
/// what lets <see cref="WindowGeometry"/> be tested without a XAML runtime, the same arrangement
/// <see cref="BatteryOrdering"/> uses.
/// </remarks>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width, including the window frame.</param>
/// <param name="Height">Height, including the window frame.</param>
internal readonly record struct WindowBounds(int X, int Y, int Width, int Height)
{
    /// <summary>The first column to the right of the rectangle.</summary>
    public int Right => X + Width;

    /// <summary>The first row below the rectangle.</summary>
    public int Bottom => Y + Height;
}

/// <summary>
/// How big the window opens, and whether a remembered position is still somewhere the user can
/// reach.
/// </summary>
/// <remarks>
/// <para>
/// Every constant here is in device-independent pixels and every value that crosses into the
/// windowing API is in physical ones, which is the whole reason this class exists. The window's
/// content is laid out in DIPs, so a readable size can only be stated in DIPs; <c>AppWindow</c>
/// moves and resizes in screen coordinates, which are physical pixels. Leaving the two conflated is
/// what produces the two failure modes this replaces — a window sized as though the display were
/// at 100% scaling, or, when nothing sizes it at all, whatever <c>CW_USEDEFAULT</c> picks, which on
/// a 4K display is most of the screen wrapped around a column of content that stops at 760 DIPs.
/// </para>
/// <para>
/// Pure and separate from the window so it can be tested against display layouts that cannot be
/// arranged on a build agent: a saved position on a monitor that is no longer attached, a saved
/// size larger than the display it lands on, a window left hanging off the bottom of the screen.
/// </para>
/// </remarks>
internal static class WindowGeometry
{
    /// <summary>Width the window opens at when nothing is remembered, in DIPs.</summary>
    /// <remarks>
    /// Chosen against the content rather than against the screen. The pages cap their content at
    /// 760 DIPs and the navigation pane takes 200, so 1150 leaves the widest page comfortably
    /// inside its cap with even margins, and the Rules page's list-beside-editor split has room for
    /// both halves. Wider would only add empty space, which is the defect being fixed.
    /// </remarks>
    public const int DefaultWidth = 1150;

    /// <summary>Height the window opens at when nothing is remembered, in DIPs.</summary>
    public const int DefaultHeight = 780;

    /// <summary>Narrowest the window may be dragged, in DIPs.</summary>
    /// <remarks>
    /// Below roughly this the navigation pane collapses over the content and the Rules page's two
    /// columns stop fitting side by side. The window is still resizable; this only rules out the
    /// sizes where the layout stops making sense.
    /// </remarks>
    public const int MinimumWidth = 900;

    /// <summary>Shortest the window may be dragged, in DIPs.</summary>
    public const int MinimumHeight = 600;

    /// <summary>
    /// How much of the window has to land on a display's work area for a remembered position to be
    /// used, in DIPs.
    /// </summary>
    /// <remarks>
    /// Enough of the title bar to grab with the mouse. The test is not "is it fully visible",
    /// because a window the user deliberately left hanging off the edge of the screen should come
    /// back where they left it; it is "can they get hold of it", which is what stops a placement
    /// saved on a monitor that has since been unplugged from opening the window somewhere with no
    /// pixels behind it.
    /// </remarks>
    public const int MinimumVisibleWidth = 180;

    /// <summary>Height of the same reachable strip, in DIPs.</summary>
    public const int MinimumVisibleHeight = 40;

    /// <summary>Convert a measurement in device-independent pixels to physical ones.</summary>
    /// <param name="dips">The measurement, in DIPs.</param>
    /// <param name="scale">The display's scale factor: 1.0 at 100%, 1.5 at 150%.</param>
    public static int Scale(int dips, double scale) => (int)Math.Round(dips * scale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Where the window opens when nothing is remembered: the default size, centred on
    /// <paramref name="workArea"/>, shrunk to fit if the display is smaller than the default.
    /// </summary>
    /// <param name="workArea">The target display's work area, in physical pixels.</param>
    /// <param name="scale">That display's scale factor.</param>
    /// <remarks>
    /// Centred on the work area rather than on the display, so the window is not partly behind the
    /// taskbar. The shrink-to-fit matters more than it looks: 1150x780 DIPs at 200% scaling is
    /// 2300x1560 physical pixels, which does not fit on a 1080p display at all.
    /// </remarks>
    public static WindowBounds Default(WindowBounds workArea, double scale)
    {
        var width = Math.Min(Scale(DefaultWidth, scale), workArea.Width);
        var height = Math.Min(Scale(DefaultHeight, scale), workArea.Height);

        return Centre(workArea, width, height);
    }

    /// <summary>
    /// The remembered placement, adjusted to the displays that are actually attached, or null if it
    /// cannot be used and the caller should fall back to <see cref="Default"/>.
    /// </summary>
    /// <param name="saved">What was read from the placement file, in physical pixels.</param>
    /// <param name="workAreas">The work area of every attached display, in physical pixels.</param>
    /// <param name="scale">The scale factor of the display the window will open on.</param>
    /// <remarks>
    /// <para>
    /// Null is returned for a placement that records no size — an empty or hand-emptied file — and
    /// for one that lands nowhere the user could click, which is what a monitor being unplugged,
    /// a resolution change, or a laptop being undocked leaves behind.
    /// </para>
    /// <para>
    /// Anything else is used, but corrected: the size is raised to the minimum and lowered to the
    /// work area it sits on, and the position is then pulled back so that a window that has grown
    /// or a display that has shrunk does not push it off the far edge. A window the user left
    /// deliberately overhanging keeps its overhang, because none of those three corrections fires
    /// on a placement that already fits.
    /// </para>
    /// </remarks>
    public static WindowBounds? Restore(WindowBounds saved, IReadOnlyList<WindowBounds> workAreas, double scale)
    {
        ArgumentNullException.ThrowIfNull(workAreas);

        if (saved.Width <= 0 || saved.Height <= 0 || workAreas.Count == 0)
        {
            return null;
        }

        // The display the window is most on, which is the one Windows would consider it to be on
        // and therefore the one whose bounds it has to fit inside.
        var host = workAreas
            .Select(area => (Area: area, Overlap: Overlap(saved, area)))
            .OrderByDescending(candidate => candidate.Overlap.Width * (long)candidate.Overlap.Height)
            .First();

        if (host.Overlap.Width < Scale(MinimumVisibleWidth, scale)
            || host.Overlap.Height < Scale(MinimumVisibleHeight, scale))
        {
            return null;
        }

        var workArea = host.Area;

        var width = Math.Clamp(saved.Width, Math.Min(Scale(MinimumWidth, scale), workArea.Width), workArea.Width);
        var height = Math.Clamp(saved.Height, Math.Min(Scale(MinimumHeight, scale), workArea.Height), workArea.Height);

        // Only pulls the window back when it now overflows: a placement that already fits is
        // returned with its position untouched, overhang and all. The pull cannot cross the near
        // edge, because the size above is already no larger than the work area.
        var x = saved.X + width > workArea.Right ? workArea.Right - width : saved.X;
        var y = saved.Y + height > workArea.Bottom ? workArea.Bottom - height : saved.Y;

        // The top is the one edge that is corrected unconditionally, and the asymmetry is
        // deliberate. A window hanging off the left or right can still be taken hold of by the part
        // of its title bar that is on screen, and a user who put it there meant it. A window whose
        // title bar is above the work area cannot be grabbed at all — Windows will not let one be
        // dragged there, so the only way to arrive here is a display that changed underneath it.
        y = Math.Max(y, workArea.Y);

        return new WindowBounds(x, y, width, height);
    }

    /// <summary>A rectangle of the given size, centred on <paramref name="workArea"/>.</summary>
    private static WindowBounds Centre(WindowBounds workArea, int width, int height) => new(
        workArea.X + ((workArea.Width - width) / 2),
        workArea.Y + ((workArea.Height - height) / 2),
        width,
        height);

    /// <summary>
    /// The overlap between two rectangles, as a width and a height. Zero in either dimension means
    /// they do not intersect.
    /// </summary>
    private static (int Width, int Height) Overlap(WindowBounds a, WindowBounds b) => (
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X)),
        Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y)));
}
