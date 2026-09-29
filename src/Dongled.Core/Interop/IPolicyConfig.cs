using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Dongled.Core.Interop;

/// <summary>
/// The undocumented interface that changes which endpoint holds a role. There is no public API
/// for this, and there never has been; every application that switches the default playback
/// device uses this interface.
/// </summary>
/// <remarks>
/// <para>
/// Only <see cref="SetDefaultEndpoint"/> is called. The other eleven slots exist because a COM
/// vtable is positional: <see cref="SetDefaultEndpoint"/> is the eleventh method, and it can
/// only be reached by declaring the ten before it.
/// </para>
/// <para>
/// Those ten are declared with their real shapes rather than as placeholder <c>int Foo()</c>
/// slots. A placeholder has the right slot count but the wrong signature, so calling one would
/// push no arguments where the callee expects pointers and corrupt the stack; with real shapes
/// the interface is safe to call rather than merely unused. Format and property arguments are typed as raw pointers because nothing marshals
/// <c>WAVEFORMATEX</c>, and an honest pointer is better than a wrong struct.
/// </para>
/// <para>
/// Because the interface is undocumented, the declaration order below is the contract. Changing
/// it moves <see cref="SetDefaultEndpoint"/> to a different slot and silently calls something
/// else; <c>PolicyConfigSignatureTests</c> pins it.
/// </para>
/// </remarks>
[GeneratedComInterface]
[Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
internal partial interface IPolicyConfig
{
    [PreserveSig]
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr ppFormat);

    [PreserveSig]
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int bDefault, out IntPtr ppFormat);

    [PreserveSig]
    int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int SetDeviceFormat(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        IntPtr pEndpointFormat,
        IntPtr pMixFormat);

    [PreserveSig]
    int GetProcessingPeriod(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        int bDefault,
        out long pmftDefaultPeriod,
        out long pmftMinimumPeriod);

    [PreserveSig]
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, in long pmftPeriod);

    [PreserveSig]
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out int pMode);

    [PreserveSig]
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, in int pMode);

    [PreserveSig]
    int GetPropertyValue(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        in PropertyKey key,
        out PropVariant pv);

    [PreserveSig]
    int SetPropertyValue(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        in PropertyKey key,
        in PropVariant pv);

    /// <summary>The one method this app calls: give <paramref name="role"/> to this endpoint.</summary>
    /// <param name="deviceId">Endpoint to make default for the role.</param>
    /// <param name="role">The role to move.</param>
    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);

    [PreserveSig]
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int bVisible);
}
