using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dongled.Core.Configuration;
using Dongled.Core.Interop;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Audio;

/// <summary>
/// <see cref="IAudioEndpointService"/> over WASAPI and <c>IPolicyConfig</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every wrapper this class creates is released in a <c>finally</c>. Each public operation
/// creates its own enumerator and releases it before returning. Holding one enumerator for the
/// lifetime of the app would save a little work, but it would also hold a raw interface pointer
/// across apartments, and enumerating a couple of dozen endpoints costs less than reasoning about
/// that.
/// </para>
/// <para>
/// Endpoint identifiers are used exactly as given, never repaired or normalized. The
/// configuration layer validates on read, so silently rewriting an identifier would hide a real
/// configuration problem behind a switch to a device the user did not name.
/// </para>
/// </remarks>
public sealed class CoreAudioEndpointService : IAudioEndpointService, IDisposable
{
    private readonly ILogger<CoreAudioEndpointService> _logger;
    private readonly EndpointNotificationClient _notificationClient;
    private readonly IMMDeviceEnumerator? _notificationEnumerator;
    private bool _disposed;

    /// <param name="logger">Where operational detail and failures are reported.</param>
    public CoreAudioEndpointService(ILogger<CoreAudioEndpointService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _notificationClient = new EndpointNotificationClient(OnDefaultDeviceChanged, logger);

        // The registration outlives every operation, so this one enumerator is kept rather than
        // created per call: unregistering has to happen through a live object, and Windows keys
        // the registration by the callback pointer.
        try
        {
            var enumerator = Com.CreateDeviceEnumerator();
            var hr = enumerator.RegisterEndpointNotificationCallback(_notificationClient);
            if (hr == 0)
            {
                _notificationEnumerator = enumerator;
            }
            else
            {
                Com.Release(enumerator);
                _logger.LogWarning(
                    "Could not subscribe to endpoint change notifications (0x{Result:X8}). The app still switches; it just will not notice changes made outside it.",
                    hr);
            }
        }
        catch (COMException ex)
        {
            // A machine whose audio service is not running must still start the app.
            _logger.LogWarning(
                ex,
                "Windows audio could not be reached while subscribing to endpoint change notifications. The app still switches; it just will not notice changes made outside it.");
        }
    }

    /// <inheritdoc />
    public event EventHandler<DefaultChangedEventArgs>? DefaultChanged;

    /// <inheritdoc />
    public IReadOnlyList<AudioEndpoint> Enumerate()
    {
        var enumerator = Com.CreateDeviceEnumerator();
        try
        {
            var defaults = ReadDefaults(enumerator);

            // ThrowExceptionForHR rather than a constructed COMException, which CA2201 reserves
            // for the runtime. It also picks a more precise type when the result maps to one, so
            // an out-of-memory failure arrives as OutOfMemoryException instead of being flattened.
            var hr = enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceState.All, out var collection);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                Marshal.ThrowExceptionForHR(collection.GetCount(out var count));

                var results = new List<AudioEndpoint>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    var itemHr = collection.Item(i, out var device);
                    if (itemHr != 0)
                    {
                        // One endpoint disappearing mid-enumeration must not lose the rest.
                        _logger.LogWarning(
                            "Endpoint {Index} of {Count} could not be read (0x{Result:X8}) and was skipped.",
                            i,
                            count,
                            itemHr);
                        continue;
                    }

                    try
                    {
                        var endpoint = Describe(device, defaults);
                        if (endpoint is not null)
                        {
                            results.Add(endpoint);
                        }
                    }
                    finally
                    {
                        Com.Release(device);
                    }
                }

                return Order(results);
            }
            finally
            {
                Com.Release(collection);
            }
        }
        finally
        {
            Com.Release(enumerator);
        }
    }

    /// <inheritdoc />
    public DefaultEndpoints GetDefaults()
    {
        var enumerator = Com.CreateDeviceEnumerator();
        try
        {
            return ReadDefaults(enumerator);
        }
        finally
        {
            Com.Release(enumerator);
        }
    }

    /// <inheritdoc />
    public bool SetDefault(string endpointId, IReadOnlyCollection<AudioRole> roles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ArgumentNullException.ThrowIfNull(roles);

        var wanted = roles.Where(Enum.IsDefined).Distinct().ToArray();
        if (wanted.Length == 0)
        {
            return true;
        }

        var before = GetDefaults();

        if (!Resolves(endpointId, out var probeResult))
        {
            // An identifier that does not resolve is never handed to IPolicyConfig.
            _logger.LogWarning(
                "Endpoint {EndpointId} does not resolve (0x{Result:X8}); not switching to it.",
                endpointId,
                probeResult);
            return false;
        }

        var policy = Com.CreatePolicyConfig();
        try
        {
            foreach (var role in wanted)
            {
                var nativeRole = ToNativeRole(role);
                try
                {
                    var hr = policy.SetDefaultEndpoint(endpointId, nativeRole);
                    if (hr != 0)
                    {
                        _logger.LogWarning(
                            "Giving the {Role} role to {EndpointId} returned 0x{Result:X8}.",
                            role,
                            endpointId,
                            hr);
                    }
                }
                catch (COMException ex)
                {
                    // Log and move on to the other role rather than abandoning both: a
                    // half-applied switch is still better than none.
                    _logger.LogError(ex, "Giving the {Role} role to {EndpointId} threw.", role, endpointId);
                }
            }
        }
        finally
        {
            Com.Release(policy);
        }

        return Verify(endpointId, wanted, before);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_notificationEnumerator is not null)
        {
            try
            {
                _notificationEnumerator.UnregisterEndpointNotificationCallback(_notificationClient);
            }
            catch (COMException ex)
            {
                _logger.LogWarning(ex, "Unsubscribing from endpoint change notifications failed.");
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogWarning(ex, "Unsubscribing from endpoint change notifications failed.");
            }

            Com.Release(_notificationEnumerator);
        }
    }

    private static ERole ToNativeRole(AudioRole role) => role switch
    {
        AudioRole.Media => ERole.Multimedia,
        AudioRole.Calls => ERole.Communications,

        // Unreachable: undefined roles are filtered out before this is called. Loud rather than
        // silent, because a new member added without a case here must not quietly do nothing.
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "No native role for this value."),
    };

    private static DefaultEndpoints ReadDefaults(IMMDeviceEnumerator enumerator) => new(
        ReadDefault(enumerator, ERole.Multimedia),
        ReadDefault(enumerator, ERole.Communications));

    private static string? ReadDefault(IMMDeviceEnumerator enumerator, ERole role)
    {
        // A failure here means no endpoint holds the role, which is the normal state of a machine
        // with no audio hardware, so the out parameter must not be used.
        var hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, role, out var device);
        if (hr != 0 || device is null)
        {
            return null;
        }

        try
        {
            return ReadId(device);
        }
        finally
        {
            Com.Release(device);
        }
    }

    private static string? ReadId(IMMDevice device)
    {
        var hr = device.GetId(out var idPtr);
        return hr != 0 ? null : Com.ReadCoTaskMemString(idPtr);
    }

    private static List<AudioEndpoint> Order(List<AudioEndpoint> endpoints) => endpoints
        .OrderByDescending(endpoint => endpoint.IsDefaultMultimedia || endpoint.IsDefaultCommunications)
        .ThenByDescending(endpoint => endpoint.IsActive)
        .ThenBy(endpoint => endpoint.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private AudioEndpoint? Describe(IMMDevice device, DefaultEndpoints defaults)
    {
        var id = ReadId(device);
        if (string.IsNullOrWhiteSpace(id))
        {
            _logger.LogWarning("An endpoint reported no identifier and was skipped.");
            return null;
        }

        var stateHr = device.GetState(out var state);
        if (stateHr != 0)
        {
            _logger.LogWarning(
                "Endpoint {EndpointId} reported no state (0x{Result:X8}); treating it as inactive.",
                id,
                stateHr);
        }

        return new AudioEndpoint(
            Id: id,
            DisplayName: ReadFriendlyName(device, id),
            IsActive: stateHr == 0 && (state & DeviceState.Active) != 0,
            IsDefaultMultimedia: string.Equals(id, defaults.MultimediaId, StringComparison.OrdinalIgnoreCase),
            IsDefaultCommunications: string.Equals(id, defaults.CommunicationsId, StringComparison.OrdinalIgnoreCase));
    }

    private string ReadFriendlyName(IMMDevice device, string id)
    {
        var storeHr = device.OpenPropertyStore(StorageAccessMode.Read, out var store);
        if (storeHr != 0)
        {
            _logger.LogWarning(
                "Endpoint {EndpointId} would not open its property store (0x{Result:X8}); it has no name to show.",
                id,
                storeHr);
            return string.Empty;
        }

        try
        {
            var key = PropertyKeys.DeviceFriendlyName;
            var valueHr = store.GetValue(in key, out var value);
            if (valueHr != 0)
            {
                _logger.LogWarning(
                    "Endpoint {EndpointId} would not report its name (0x{Result:X8}).",
                    id,
                    valueHr);
                return string.Empty;
            }

            try
            {
                return value.AsString() ?? string.Empty;
            }
            finally
            {
                Ole32.PropVariantClear(ref value);
            }
        }
        finally
        {
            Com.Release(store);
        }
    }

    private static bool Resolves(string endpointId, out int result)
    {
        var enumerator = Com.CreateDeviceEnumerator();
        try
        {
            result = enumerator.GetDevice(endpointId, out var device);
            Com.Release(device);
            return result == 0;
        }
        finally
        {
            Com.Release(enumerator);
        }
    }

    private bool Verify(string endpointId, IReadOnlyCollection<AudioRole> wanted, DefaultEndpoints before)
    {
        DefaultEndpoints after;
        try
        {
            after = GetDefaults();
        }
        catch (Exception ex)
        {
            // Broad on purpose: ThrowExceptionForHR translates some results to types that are not
            // COMException, and not being able to check is not the same as having failed however
            // it went wrong. This reports success and says why it could not be confirmed.
            _logger.LogWarning(ex, "Could not confirm that {EndpointId} took the roles it was given.", endpointId);
            return true;
        }

        var applied = true;

        if (wanted.Contains(AudioRole.Media)
            && !string.Equals(after.MultimediaId, endpointId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "The Media role did not move to {Requested}. Before={Before} After={After}",
                endpointId,
                before.MultimediaId,
                after.MultimediaId);
            applied = false;
        }

        if (wanted.Contains(AudioRole.Calls)
            && !string.Equals(after.CommunicationsId, endpointId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "The Calls role did not move to {Requested}. Before={Before} After={After}",
                endpointId,
                before.CommunicationsId,
                after.CommunicationsId);
            applied = false;
        }

        return applied;
    }

    private void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? endpointId)
    {
        if (flow != EDataFlow.Render)
        {
            return;
        }

        // The console role is read about and never assigned, so it is not surfaced either: a
        // Status page row for a role the app does not manage would only raise questions.
        var mapped = role switch
        {
            ERole.Multimedia => (AudioRole?)AudioRole.Media,
            ERole.Communications => AudioRole.Calls,
            _ => null,
        };

        if (mapped is null)
        {
            return;
        }

        DefaultChanged?.Invoke(this, new DefaultChangedEventArgs(mapped.Value, endpointId));
    }
}
