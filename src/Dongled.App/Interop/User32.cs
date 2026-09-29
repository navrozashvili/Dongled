using System.Runtime.InteropServices;

namespace Dongled.App.Interop;

/// <summary>Window calls the framework does not surface.</summary>
internal static partial class User32
{
    /// <summary>Dots per inch at 100% scaling, which is what every scale factor is relative to.</summary>
    internal const double DefaultDpi = 96.0;

    /// <summary>
    /// The scale factor of the display a window is on: 1.0 at 100%, 1.5 at 150%.
    /// </summary>
    /// <param name="hwnd">The window handle.</param>
    /// <remarks>
    /// <para>
    /// There is no managed route to this. <c>AppWindow</c> works in physical pixels and never
    /// mentions scaling, and the XAML side's <c>XamlRoot.RasterizationScale</c> — which is the same
    /// number — does not exist until the window has been activated, which is too late for a window
    /// that has to be sized before it is first shown, and never at all for one that starts hidden.
    /// </para>
    /// <para>
    /// Falls back to 1.0 if Windows reports no DPI, which it does for a handle that is not a window
    /// yet. That is the right failure: the window opens at its default size treated as physical
    /// pixels, which is small on a scaled display but reachable and resizable.
    /// </para>
    /// </remarks>
    internal static double ScaleFor(nint hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);

        return dpi <= 0 ? 1.0 : dpi / DefaultDpi;
    }

    /// <remarks>
    /// Pinned to <see cref="DllImportSearchPath.System32"/> for the same reason as
    /// <see cref="Dongled.App.Tray.NativeMethods.GetSystemMetrics"/>: without it the
    /// search order includes the application directory, so a planted <c>user32.dll</c> beside a
    /// user-writable install would be loaded ahead of the real one.
    /// </remarks>
    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetDpiForWindow(nint hwnd);
}
