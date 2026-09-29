using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Dongled.Core.Interop;

/// <summary>Native <c>IPropertyStore</c>. Only <see cref="GetValue"/> is used.</summary>
[GeneratedComInterface]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
internal partial interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint cProps);

    [PreserveSig]
    int GetAt(uint iProp, out PropertyKey pkey);

    [PreserveSig]
    int GetValue(in PropertyKey key, out PropVariant pv);

    [PreserveSig]
    int SetValue(in PropertyKey key, in PropVariant pv);

    [PreserveSig]
    int Commit();
}

/// <summary>Native <c>IMMDevice</c>.</summary>
[GeneratedComInterface]
[Guid("d666063f-1587-4e43-81f1-b948e807363f")]
internal partial interface IMMDevice
{
    /// <param name="iid">Interface to activate.</param>
    /// <param name="dwClsCtx">Execution context.</param>
    /// <param name="pActivationParams">Activation parameters, or zero.</param>
    /// <param name="ppInterface">
    /// Raw pointer rather than an interface type, because nothing here activates a device
    /// interface and typing it would commit the declaration to one.
    /// </param>
    [PreserveSig]
    int Activate(in Guid iid, uint dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);

    [PreserveSig]
    int OpenPropertyStore(StorageAccessMode stgmAccess, out IPropertyStore ppProperties);

    /// <param name="ppstrId">
    /// A string the callee allocated with <c>CoTaskMemAlloc</c>. The caller frees it; see
    /// <see cref="Com.ReadCoTaskMemString"/>.
    /// </param>
    [PreserveSig]
    int GetId(out IntPtr ppstrId);

    [PreserveSig]
    int GetState(out DeviceState pdwState);
}

/// <summary>Native <c>IMMDeviceCollection</c>.</summary>
[GeneratedComInterface]
[Guid("0bd7a1be-7a1a-44db-8397-cc5392387b5e")]
internal partial interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint pcDevices);

    /// <param name="nDevice">Index into the collection.</param>
    /// <param name="ppDevice">
    /// A wrapper of its own, which the caller must release. Verified: interface-typed out
    /// parameters come back as distinct <c>ComObject</c> instances.
    /// </param>
    [PreserveSig]
    int Item(uint nDevice, out IMMDevice ppDevice);
}

/// <summary>
/// Native <c>IMMNotificationClient</c>: how Windows tells the app about endpoint changes made
/// outside it. Implemented by the host, not called by it.
/// </summary>
/// <remarks>
/// <para>
/// Every member is <c>[PreserveSig]</c>, which means the returned <c>int</c> <em>is</em> the
/// HRESULT and no exception translation happens. An exception escaping one of these into the
/// caller's native frame is undefined behaviour, so an implementation must catch everything and
/// return a result instead.
/// </para>
/// <para>
/// Windows calls these on its own threads, never on one this app created.
/// </para>
/// </remarks>
[GeneratedComInterface]
[Guid("7991eec9-7e89-4d85-8390-6c703cec60c0")]
internal partial interface IMMNotificationClient
{
    [PreserveSig]
    int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string? pwstrDeviceId, DeviceState dwNewState);

    [PreserveSig]
    int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string? pwstrDeviceId);

    [PreserveSig]
    int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string? pwstrDeviceId);

    /// <param name="flow">Playback or recording.</param>
    /// <param name="role">The role that changed hands.</param>
    /// <param name="pwstrDefaultDeviceId">
    /// Nullable on purpose: Windows passes NULL when no endpoint holds the role any more, for
    /// example when the last playback device is unplugged.
    /// </param>
    [PreserveSig]
    int OnDefaultDeviceChanged(
        EDataFlow flow,
        ERole role,
        [MarshalAs(UnmanagedType.LPWStr)] string? pwstrDefaultDeviceId);

    [PreserveSig]
    int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string? pwstrDeviceId, PropertyKey key);
}

/// <summary>Native <c>IMMDeviceEnumerator</c>.</summary>
[GeneratedComInterface]
[Guid("a95664d2-9614-4f35-a746-de8db63617e6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState dwStateMask, out IMMDeviceCollection ppDevices);

    /// <param name="dataFlow">Playback or recording.</param>
    /// <param name="role">Which default to read.</param>
    /// <param name="ppEndpoint">
    /// Null when no endpoint holds the role, in which case the result is a failure HRESULT. Check
    /// the result before using it.
    /// </param>
    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? ppEndpoint);

    /// <param name="pwstrId">Endpoint identifier to look up.</param>
    /// <param name="ppDevice">The endpoint, or null if the identifier did not resolve.</param>
    /// <remarks>
    /// A null or empty identifier is reported as a failure result rather than thrown, so
    /// validation is the caller's job.
    /// </remarks>
    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice? ppDevice);

    /// <param name="pClient">The callback to register.</param>
    /// <remarks>
    /// Typed as the interface rather than <see cref="IntPtr"/>, so a managed callback can be
    /// passed. A <c>[GeneratedComClass]</c> implementation marshals directly, and the same
    /// managed instance produces the same COM wrapper every time, so the
    /// pointer registered here is the pointer
    /// <see cref="UnregisterEndpointNotificationCallback"/> removes.
    /// </remarks>
    [PreserveSig]
    int RegisterEndpointNotificationCallback(IMMNotificationClient pClient);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
}
