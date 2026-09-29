using Dongled.Abstractions;

namespace Dongled.Plugin.Logitech;

/// <summary>
/// Turns what G HUB said into what the host understands: the sources a user can write a rule against,
/// and whether each is here.
/// </summary>
/// <remarks>
/// <para>
/// Three source shapes: one aggregate, one per model, one per device. The per-device shape keys on
/// <see cref="GhubDevice.Signature"/> rather than on <see cref="GhubDevice.Id"/>, because the id is
/// positional and a rule persisted against one would point at a different device after G HUB
/// re-enumerated.
/// </para>
/// <para>
/// Every known device is described whether or not it is switched on. Presence is what says whether it
/// is here; a device vanishing from the rules picker because it was charging would be worse.
/// </para>
/// </remarks>
internal static class GhubCatalogue
{
    /// <summary>Any Logitech device G HUB reports as connected.</summary>
    public const string AnySourceId = "logitech:ghub:any";

    private const string ModelPrefix = "logitech:ghub:model:";
    private const string SignaturePrefix = "logitech:ghub:signature:";

    /// <summary>
    /// The source id a device's signature maps to. Public so callers outside this class - the battery
    /// poll, which learns a device's signature from the same <see cref="GhubDevice"/> map this class
    /// builds sources from - can address the same source without duplicating the prefix.
    /// </summary>
    public static string SignatureSourceId(string signature) => SignaturePrefix + signature;

    private static readonly AudioSourceDescriptor AnySource = new(
        AnySourceId,
        "Any Logitech device",
        "any device G HUB reports as connected");

    /// <summary>
    /// The complete set of sources these devices offer, in a stable order so an unchanged set compares
    /// equal to the previously published one.
    /// </summary>
    public static IReadOnlyList<AudioSourceDescriptor> Describe(IEnumerable<GhubDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var byId = new Dictionary<string, AudioSourceDescriptor>(StringComparer.Ordinal);

        foreach (var device in devices)
        {
            if (device.DeviceModel.Length > 0)
            {
                // Not the unslugged model: measured, G HUB's own deviceModel is already a slug
                // ("g502x_plus") and displayName is the field that is not ("G502 X PLUS").
                var name = FirstNonEmpty(device.DisplayName, device.DeviceModel);
                byId[ModelPrefix + device.DeviceModel] = new AudioSourceDescriptor(
                    ModelPrefix + device.DeviceModel,
                    name,
                    $"any {name} in G HUB");
            }

            if (device.Signature.Length > 0)
            {
                byId[SignatureSourceId(device.Signature)] = new AudioSourceDescriptor(
                    SignatureSourceId(device.Signature),
                    DisplayNameOf(device),
                    device.DeviceType.Length > 0 ? $"G HUB · {device.DeviceType}" : "G HUB");
            }
        }

        return
        [
            AnySource,
            .. byId.Values.OrderBy(d => d.SourceId, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Whether each source is here. Every source <see cref="Describe"/> would return gets an answer, and
    /// the answer is never <see cref="Presence.Unknown"/>.
    /// </summary>
    /// <param name="devices">Everything G HUB currently knows about.</param>
    /// <param name="connectedStates">
    /// The states that count as connected, matched case-insensitively. Configurable because the agent's
    /// vocabulary is undocumented and has changed between G HUB builds.
    /// </param>
    public static IReadOnlyDictionary<string, Presence> Presences(
        IEnumerable<GhubDevice> devices,
        IReadOnlyList<string> connectedStates)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(connectedStates);

        var presences = new Dictionary<string, Presence>(StringComparer.Ordinal)
        {
            [AnySourceId] = Presence.Absent,
        };

        foreach (var device in devices)
        {
            var connected = IsConnected(device.State, connectedStates);

            if (connected)
            {
                presences[AnySourceId] = Presence.Present;
            }

            if (device.DeviceModel.Length > 0)
            {
                var key = ModelPrefix + device.DeviceModel;

                // Any device of a model makes the model present, so two of one model do not overwrite
                // each other depending on enumeration order.
                presences[key] = connected || presences.GetValueOrDefault(key) == Presence.Present
                    ? Presence.Present
                    : Presence.Absent;
            }

            if (device.Signature.Length > 0)
            {
                presences[SignatureSourceId(device.Signature)] = connected
                    ? Presence.Present
                    : Presence.Absent;
            }
        }

        return presences;
    }

    private static bool IsConnected(string state, IReadOnlyList<string> connectedStates)
    {
        for (var i = 0; i < connectedStates.Count; i++)
        {
            if (string.Equals(state, connectedStates[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The friendliest name this device offers. Best-effort with a fallback chain, so it is always
    /// non-empty; the id is a last resort rather than an identifier used as a name.
    /// </summary>
    private static string DisplayNameOf(GhubDevice device) =>
        FirstNonEmpty(device.ExtendedDisplayName, device.DisplayName, device.DeviceModel, device.Id);

    private static string FirstNonEmpty(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }
}
