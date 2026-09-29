using System.Collections.Generic;
using System.Linq;
using Dongled.Core.Audio;
using Dongled.Core.Pipeline;

namespace Dongled.App.Presentation;

/// <summary>
/// Turns identifiers into something a person can read, and is the only place allowed to decide
/// what to show when that is not possible.
/// </summary>
/// <remarks>
/// <para>
/// No raw source identifier and no device GUID is ever shown in the UI. That is easy to
/// satisfy while a device is plugged in and easy to breach the moment it is not, because the
/// obvious fallback for "I cannot find a name for this" is to print the key. So every method here
/// ends in a phrase rather than an identifier, and none of them has a path that returns its input.
/// </para>
/// <para>
/// The fallback order is the same in each case: what a provider or Windows currently calls it, then
/// the <c>lastKnownName</c> the configuration recorded when it was last seen, then a phrase saying
/// it cannot be named.
/// </para>
/// </remarks>
internal static class DisplayNames
{
    /// <summary>Shown in place of a device that is configured but cannot be named at all.</summary>
    public const string UnnameableDevice = "a device that is no longer available";

    /// <summary>Shown in place of a source that is configured but cannot be named at all.</summary>
    public const string UnnameableSource = "a source no enabled plugin publishes";

    /// <summary>Shown where a role is held by nothing.</summary>
    public const string NoDevice = "None";

    /// <summary>Name a playback endpoint.</summary>
    /// <param name="endpoints">The current enumeration, or empty if it could not be read.</param>
    /// <param name="deviceId">The endpoint identifier from a rule, or null.</param>
    /// <param name="lastKnownName">The name configuration recorded for it, if any.</param>
    public static string ForDevice(
        IReadOnlyList<AudioEndpoint> endpoints,
        string? deviceId,
        string? lastKnownName)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return NoDevice;
        }

        var live = endpoints.FirstOrDefault(
            endpoint => string.Equals(endpoint.Id, deviceId, StringComparison.OrdinalIgnoreCase));

        if (live is not null && !string.IsNullOrWhiteSpace(live.DisplayName))
        {
            // An endpoint that exists but is switched off or unplugged is still worth naming; the
            // rules pages say so beside the name rather than hiding it.
            return live.IsActive
                ? live.DisplayName
                : $"{live.DisplayName} — not currently connected";
        }

        return string.IsNullOrWhiteSpace(lastKnownName)
            ? UnnameableDevice
            : $"{lastKnownName} — not currently connected";
    }

    /// <summary>Name the endpoint currently holding a role.</summary>
    /// <param name="endpoints">The current enumeration.</param>
    /// <param name="endpointId">The endpoint holding the role, or null if none does.</param>
    public static string ForDefault(IReadOnlyList<AudioEndpoint> endpoints, string? endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return NoDevice;
        }

        var live = endpoints.FirstOrDefault(
            endpoint => string.Equals(endpoint.Id, endpointId, StringComparison.OrdinalIgnoreCase));

        return live is not null && !string.IsNullOrWhiteSpace(live.DisplayName)
            ? live.DisplayName
            : UnnameableDevice;
    }

    /// <summary>Name an audio source.</summary>
    /// <param name="sources">What providers currently publish.</param>
    /// <param name="sourceId">The source identifier from a rule, or null.</param>
    /// <param name="lastKnownName">The name configuration recorded for it, if any.</param>
    public static string ForSource(
        IReadOnlyList<SourceState> sources,
        string? sourceId,
        string? lastKnownName)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return UnnameableSource;
        }

        var published = sources.FirstOrDefault(
            state => string.Equals(state.Descriptor.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));

        if (published is not null && !string.IsNullOrWhiteSpace(published.Descriptor.DisplayName))
        {
            return published.Descriptor.DisplayName;
        }

        return string.IsNullOrWhiteSpace(lastKnownName) ? UnnameableSource : lastKnownName;
    }
}
