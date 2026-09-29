using System.Runtime.InteropServices.Marshalling;
using Dongled.Core.Interop;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Audio;

/// <summary>
/// Turns Windows endpoint notifications into a managed callback. Without this the Status page
/// would show the last thing the app did rather than what is actually true.
/// </summary>
/// <remarks>
/// <para>
/// Every member returns <c>S_OK</c> whatever happens. The interface is declared
/// <c>[PreserveSig]</c>, so an exception escaping one of these methods would unwind into a
/// native Windows frame, which is undefined behaviour rather than an error someone sees. The
/// catch is therefore load-bearing, not defensive habit.
/// </para>
/// <para>
/// Windows calls these on its own threads. The callback this forwards to must not assume
/// otherwise.
/// </para>
/// </remarks>
[GeneratedComClass]
internal sealed partial class EndpointNotificationClient : IMMNotificationClient
{
    private const int SOk = 0;

    private readonly Action<EDataFlow, ERole, string?> _onDefaultDeviceChanged;
    private readonly ILogger _logger;

    internal EndpointNotificationClient(
        Action<EDataFlow, ERole, string?> onDefaultDeviceChanged,
        ILogger logger)
    {
        _onDefaultDeviceChanged = onDefaultDeviceChanged;
        _logger = logger;
    }

    /// <inheritdoc />
    public int OnDeviceStateChanged(string? pwstrDeviceId, DeviceState dwNewState) => SOk;

    /// <inheritdoc />
    public int OnDeviceAdded(string? pwstrDeviceId) => SOk;

    /// <inheritdoc />
    public int OnDeviceRemoved(string? pwstrDeviceId) => SOk;

    /// <inheritdoc />
    public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? pwstrDefaultDeviceId)
    {
        try
        {
            _onDefaultDeviceChanged(flow, role, pwstrDefaultDeviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handling a default endpoint change notification threw.");
        }

        return SOk;
    }

    /// <inheritdoc />
    public int OnPropertyValueChanged(string? pwstrDeviceId, PropertyKey key) => SOk;
}
