using System.Runtime.InteropServices;

namespace Dongled.Core.Interop;

/// <summary>The <c>ole32</c> entry points this app needs.</summary>
/// <remarks>
/// Both imports pin the loader to <see cref="DllImportSearchPath.System32"/>. Without it the
/// search order includes the application directory, so a planted <c>ole32.dll</c> beside a
/// user-writable install would be loaded in preference to the real one.
/// </remarks>
internal static partial class Ole32
{
    /// <summary><c>CLSCTX_INPROC_SERVER</c>.</summary>
    internal const uint ClsCtxInprocServer = 0x1;

    /// <summary><c>IID_IUnknown</c>.</summary>
    internal static readonly Guid IidIUnknown = new("00000000-0000-0000-c000-000000000046");

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int CoCreateInstance(
        in Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out IntPtr ppv);

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int PropVariantClear(ref PropVariant pvar);
}
