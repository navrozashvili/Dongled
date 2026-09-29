using System.Collections.Generic;
using System.Linq;
using Dongled.Core.Audio;
using Dongled.Core.Configuration;

namespace Dongled.Core.Policy;

/// <summary>
/// What a rule's target means once it may be a name pattern rather than one endpoint identifier.
/// </summary>
internal static class RuleDevices
{
    /// <summary>Whether a pattern field holds a pattern at all.</summary>
    internal static bool HasPattern(string? pattern) => !string.IsNullOrWhiteSpace(pattern);

    /// <summary>Whether the rule names something to switch to, by identifier or by pattern.</summary>
    internal static bool HasTarget(Rule rule) =>
        HasPattern(rule.Target.NamePattern) || !string.IsNullOrWhiteSpace(rule.Target.DeviceId);

    /// <summary>
    /// Whether <paramref name="deviceId"/> is the rule's target. With a pattern that is any endpoint
    /// whose name matches, connected or not, since recognising the target is a different question
    /// from choosing one to switch to; without, it is the stored identifier.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <param name="deviceId">The endpoint in question.</param>
    /// <param name="endpoints">
    /// The current enumeration. Only consulted when the rule has a pattern, so a caller may pass an
    /// empty list otherwise.
    /// </param>
    internal static bool IsTarget(Rule rule, string? deviceId, IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        var pattern = rule.Target.NamePattern;
        if (!HasPattern(pattern))
        {
            return string.Equals(deviceId, rule.Target.DeviceId, StringComparison.OrdinalIgnoreCase);
        }

        var endpoint = endpoints.FirstOrDefault(
            candidate => string.Equals(candidate.Id, deviceId, StringComparison.OrdinalIgnoreCase));

        return endpoint is not null && DeviceNameMatcher.IsMatch(pattern!, endpoint.DisplayName ?? string.Empty);
    }
}
