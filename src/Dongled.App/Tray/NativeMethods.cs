using System.Runtime.InteropServices;

namespace Dongled.App.Tray;

/// <summary>Win32 calls the shell needs and the framework does not surface.</summary>
internal static partial class NativeMethods
{
    /// <summary>Width of a small icon, which is what the notification area asks for.</summary>
    /// <remarks>
    /// 16 at 100% scaling and 32 at 200%. Reading it rather than assuming 16 is what keeps the
    /// digits crisp on a scaled display instead of letting Windows upscale a 16-pixel bitmap.
    /// </remarks>
    internal const int SM_CXSMICON = 49;

    /// <remarks>
    /// Pinned to <see cref="DllImportSearchPath.System32"/> for the same reason as
    /// <see cref="Dongled.Core.Interop.Ole32"/>'s imports and this project's own
    /// <c>BatteryTrayIconRenderer.DestroyIcon</c>: without it, the search order includes the
    /// application directory, so a planted <c>user32.dll</c> beside a user-writable install would be
    /// loaded ahead of the real one. <c>user32.dll</c> is a genuine system library, so System32 is
    /// both correct and the safest choice here.
    /// </remarks>
    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int GetSystemMetrics(int index);
}
