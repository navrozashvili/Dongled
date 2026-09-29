using System.Globalization;
using System.Text.Json;

namespace Dongled.Plugin.Logitech;

/// <summary>One device, as much of it as this plugin reads.</summary>
/// <remarks>
/// <para>
/// Every string member is non-null and empty when the agent did not supply it, so a merge can test for
/// emptiness in one place instead of juggling nulls.
/// </para>
/// <para>
/// <paramref name="Signature"/> is the stable identifier, not <paramref name="Id"/>. Measured: ids are
/// positional (<c>dev00000000</c>, <c>dev00000001</c>, <c>dev00000002</c> in enumeration order) while a
/// signature embeds the device's unit id, for example <c>MOUSE.g502x_plus.0.3394205497</c>. The id is
/// still what a delta keys on, because that is what the agent sends within one session.
/// </para>
/// </remarks>
internal sealed record GhubDevice(
    string Id,
    string State,
    string DeviceType,
    string DeviceModel,
    string DisplayName,
    string ExtendedDisplayName,
    string Signature);

/// <summary>One device's battery, as much of it as this plugin reads.</summary>
/// <param name="DeviceId">The session-local device id the reply names.</param>
/// <param name="Percent">Charge remaining 0 to 100, or null if the agent did not give one.</param>
/// <param name="Charging">Whether it is filling.</param>
/// <param name="FullyCharged">Whether the agent considers it done. Reported separately from
/// <paramref name="Charging"/>, which goes false once it is.</param>
internal sealed record GhubBattery(string DeviceId, int? Percent, bool Charging, bool FullyCharged);

/// <summary>What a parsed message says about the set of devices.</summary>
internal enum GhubUpdate
{
    /// <summary>Nothing to act on: not a message, not a path this plugin listens to, or unusable.</summary>
    None = 0,

    /// <summary>The complete set, which replaces whatever was known.</summary>
    FullList = 1,

    /// <summary>Some devices changed; merge them into what is known.</summary>
    Delta = 2,
}

/// <summary>
/// The agent's wire format. Pure, and the parsing half never throws whatever it is handed.
/// </summary>
internal static class GhubMessages
{
    /// <summary>The path a full device list arrives on, and the one a request asks for.</summary>
    public const string DevicesListPath = "/devices/list";

    /// <summary>The path state changes arrive on, once subscribed.</summary>
    public const string StateChangedPath = "/devices/state/changed";

    /// <summary>
    /// Serialise a request. The message id key is <c>msg_id</c>.
    /// </summary>
    /// <remarks>
    /// Measured against lghub_agent on 2026-07-27: a <c>GET /devices/list</c> sent with <c>msg_id</c>
    /// was answered <c>"result": {"code": "SUCCESS"}</c> with the id echoed back as <c>msgId</c>, and
    /// the agent's own hello uses <c>msgId</c>. The key a request uses does not matter, so there is no
    /// need to detect which one the agent prefers. Either key would work here; this one is a choice,
    /// not a requirement.
    /// </remarks>
    public static string Request(string verb, string path, long messageId)
    {
        var message = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["msg_id"] = "asw:" + messageId.ToString(CultureInfo.InvariantCulture),
            ["verb"] = verb,
            ["path"] = path,
        };

        return JsonSerializer.Serialize(message);
    }

    /// <summary>The path a device's battery state is read from.</summary>
    /// <remarks>
    /// Measured against lghub_agent on 2026-07-28. Takes the session-local device id
    /// (<c>dev00000000</c>), not the signature: the agent keys this by id.
    /// </remarks>
    public static string BatteryPath(string deviceId) => "/battery/" + deviceId + "/state";

    /// <summary>
    /// Read a battery reply, if that is what this message is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns false for anything else, including the agent's <c>NO_SUCH_PATH</c> answer. That
    /// answer is not an error to report: measured on 2026-07-28, the agent gives it for a device
    /// with no battery, for a device that is currently disconnected, and for an unknown id alike, so
    /// a successful read is the only capability test there is.
    /// </para>
    /// <para>
    /// The <c>path</c> is checked, not just <c>result.code</c> and the shape of <c>payload</c>: a
    /// <c>SUCCESS</c> reply on some other path that happens to carry a string <c>deviceId</c> is not
    /// a battery reply, and misreading one as one would be sticky - it would add a phantom entry to
    /// the provider's remembered battery sources that never goes away for the rest of the process.
    /// </para>
    /// <para>Never throws, whatever it is handed. Same contract as the rest of the parsing here.</para>
    /// </remarks>
    public static bool TryReadBattery(string message, out GhubBattery? battery)
    {
        battery = null;

        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("result", out var result)
                || !result.TryGetProperty("code", out var code)
                || code.ValueKind != JsonValueKind.String
                || !string.Equals(code.GetString(), "SUCCESS", StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!payload.TryGetProperty("deviceId", out var id)
                || id.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id.GetString()))
            {
                return false;
            }

            int? percent = null;
            if (payload.TryGetProperty("percentage", out var pct)
                && pct.ValueKind == JsonValueKind.Number
                && pct.TryGetInt32(out var value))
            {
                // Refused rather than clamped: a figure outside the range means this is not the
                // message it appears to be, and a clamped guess shown as a percentage is worse than
                // no percentage.
                if (value is < 0 or > 100)
                {
                    return false;
                }

                percent = value;
            }

            // A SUCCESS reply on some other path that happens to carry a string deviceId is not a
            // battery reply, and misreading one as one would be sticky - it would add a phantom entry
            // to the provider's remembered battery sources that never goes away for the rest of the
            // process. The order against the checks above is not load-bearing - refusal is refusal -
            // but this is what a doubly-wrong message (an unrelated path with a wrong-typed
            // percentage) exercises on its way to being turned away.
            if (!TryReadString(root, "path", out var path)
                || !path.StartsWith("/battery/", StringComparison.Ordinal))
            {
                return false;
            }

            battery = new GhubBattery(
                id.GetString()!,
                percent,
                Charging: payload.TryGetProperty("charging", out var charging)
                    && charging.ValueKind == JsonValueKind.True,
                FullyCharged: payload.TryGetProperty("fullyCharged", out var full)
                    && full.ValueKind == JsonValueKind.True);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Read a message from the agent.
    /// </summary>
    /// <remarks>
    /// Never throws. Anything that is not a message this plugin acts on - including text that is not
    /// JSON at all - answers <see cref="GhubUpdate.None"/> with no devices. A device entry with no
    /// usable id is dropped, because it cannot be keyed, merged or reported on; an entry whose other
    /// fields are the wrong type keeps its id and loses only those fields.
    /// </remarks>
    public static GhubUpdate TryReadDevices(string json, out IReadOnlyList<GhubDevice> devices)
    {
        devices = [];

        if (string.IsNullOrWhiteSpace(json))
        {
            return GhubUpdate.None;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !TryReadString(root, "path", out var path)
                || path.Length == 0
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object)
            {
                return GhubUpdate.None;
            }

            if (string.Equals(path, DevicesListPath, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadArray(payload, "deviceInfos", out var entries))
                {
                    return GhubUpdate.None;
                }

                devices = ReadDevices(entries);

                // An empty list is real information - everything previously known is gone - so it is a
                // full list of nothing rather than "no answer".
                return GhubUpdate.FullList;
            }

            if (string.Equals(path, StateChangedPath, StringComparison.OrdinalIgnoreCase))
            {
                // Some builds send one deviceInfo, others a list. Be liberal about which.
                if (payload.TryGetProperty("deviceInfo", out var single)
                    && single.ValueKind == JsonValueKind.Object)
                {
                    devices = ReadDevices([single]);
                    return devices.Count == 0 ? GhubUpdate.None : GhubUpdate.Delta;
                }

                if (TryReadArray(payload, "deviceInfos", out var entries))
                {
                    devices = ReadDevices(entries);
                    return devices.Count == 0 ? GhubUpdate.None : GhubUpdate.Delta;
                }
            }

            return GhubUpdate.None;
        }
        catch (JsonException)
        {
            return GhubUpdate.None;
        }
    }

    private static List<GhubDevice> ReadDevices(IEnumerable<JsonElement> entries)
    {
        var devices = new List<GhubDevice>();

        foreach (var entry in entries)
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !TryReadString(entry, "id", out var id)
                || id.Length == 0)
            {
                continue;
            }

            devices.Add(new GhubDevice(
                Id: id,
                State: ReadString(entry, "state"),
                DeviceType: ReadString(entry, "deviceType"),
                DeviceModel: ReadString(entry, "deviceModel"),
                DisplayName: ReadString(entry, "displayName"),
                ExtendedDisplayName: ReadString(entry, "extendedDisplayName"),
                Signature: ReadString(entry, "deviceSignature")));
        }

        return devices;
    }

    private static bool TryReadArray(JsonElement parent, string name, out List<JsonElement> entries)
    {
        entries = [];

        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        entries = [.. value.EnumerateArray()];
        return true;
    }

    private static bool TryReadString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;

        // The ValueKind test is what keeps a wrong-typed field from throwing: GetString() on a number
        // throws InvalidOperationException, which is not a JsonException and would escape the catch.
        if (!parent.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static string ReadString(JsonElement parent, string name) =>
        TryReadString(parent, name, out var value) ? value : string.Empty;
}
