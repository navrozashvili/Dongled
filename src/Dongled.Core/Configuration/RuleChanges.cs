using System.Collections.Generic;
using System.Linq;

namespace Dongled.Core.Configuration;

/// <summary>
/// Describes what changed between two sets of rules, one sentence per change.
/// </summary>
/// <remarks>
/// Logged on every configuration reload, so that when a rule's target moves to a different device
/// the log shows whether that was a deliberate edit. Devices and sources are given by name and by
/// identifier: the name is what
/// the user recognises, the identifier is what the engine acts on, and a diagnosis needs both.
/// A <c>lastKnownName</c> changing on its own is not reported; it is a cache, not a decision.
/// </remarks>
internal static class RuleChanges
{
    private const string None = "(none)";

    /// <summary>Every difference between <paramref name="before"/> and <paramref name="after"/>.</summary>
    /// <param name="before">The rules as they were.</param>
    /// <param name="after">The rules as they are now.</param>
    public static IReadOnlyList<string> Describe(IReadOnlyList<Rule> before, IReadOnlyList<Rule> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changes = new List<string>();

        foreach (var old in before)
        {
            var current = after.FirstOrDefault(rule => string.Equals(rule.Id, old.Id, StringComparison.Ordinal));
            if (current is null)
            {
                changes.Add($"Rule {Label(old)}: removed.");
                continue;
            }

            DescribeOne(old, current, changes);
        }

        foreach (var rule in after)
        {
            if (!before.Any(old => string.Equals(old.Id, rule.Id, StringComparison.Ordinal)))
            {
                changes.Add($"Rule {Label(rule)}: added.");
            }
        }

        return changes;
    }

    private static void DescribeOne(Rule old, Rule current, List<string> changes)
    {
        if (!string.Equals(old.Name, current.Name, StringComparison.Ordinal))
        {
            changes.Add($"Rule {Label(old)}: renamed to {Label(current)}.");
        }

        var label = Label(current);

        if (old.Enabled != current.Enabled)
        {
            changes.Add($"Rule {label}: switched {(current.Enabled ? "on" : "off")}.");
        }

        if (!SameId(old.Source.Id, current.Source.Id))
        {
            changes.Add(
                $"Rule {label}: source changed from {Named(old.Source.Id, old.Source.LastKnownName)} to {Named(current.Source.Id, current.Source.LastKnownName)}.");
        }

        if (!SameId(old.Target.DeviceId, current.Target.DeviceId))
        {
            changes.Add(
                $"Rule {label}: target changed from {Named(old.Target.DeviceId, old.Target.LastKnownName)} to {Named(current.Target.DeviceId, current.Target.LastKnownName)}.");
        }

        if (!SamePattern(old.Target.NamePattern, current.Target.NamePattern))
        {
            changes.Add(
                $"Rule {label}: target name pattern changed from {Pattern(old.Target.NamePattern)} to {Pattern(current.Target.NamePattern)}.");
        }

        var oldRoles = Roles(old.Target.Roles);
        var newRoles = Roles(current.Target.Roles);
        if (!string.Equals(oldRoles, newRoles, StringComparison.Ordinal))
        {
            changes.Add($"Rule {label}: roles changed from {oldRoles} to {newRoles}.");
        }

        if (old.OnDisconnect.Mode != current.OnDisconnect.Mode)
        {
            changes.Add(
                $"Rule {label}: disconnect behaviour changed from {old.OnDisconnect.Mode} to {current.OnDisconnect.Mode}.");
        }

        if (!SameId(old.OnDisconnect.FallbackDeviceId, current.OnDisconnect.FallbackDeviceId))
        {
            changes.Add(
                $"Rule {label}: fallback changed from {Named(old.OnDisconnect.FallbackDeviceId, old.OnDisconnect.FallbackLastKnownName)} to {Named(current.OnDisconnect.FallbackDeviceId, current.OnDisconnect.FallbackLastKnownName)}.");
        }

        if (!SamePattern(old.OnDisconnect.FallbackNamePattern, current.OnDisconnect.FallbackNamePattern))
        {
            changes.Add(
                $"Rule {label}: fallback name pattern changed from {Pattern(old.OnDisconnect.FallbackNamePattern)} to {Pattern(current.OnDisconnect.FallbackNamePattern)}.");
        }
    }

    private static string Label(Rule rule) => string.IsNullOrWhiteSpace(rule.Name) ? rule.Id : rule.Name;

    private static bool SameId(string? a, string? b) =>
        string.Equals(Blank(a), Blank(b), StringComparison.OrdinalIgnoreCase);

    private static bool SamePattern(string? a, string? b) =>
        string.Equals(Blank(a), Blank(b), StringComparison.Ordinal);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Named(string? id, string? name) =>
        string.IsNullOrWhiteSpace(id)
            ? None
            : $"{(string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name)} [{id}]";

    private static string Pattern(string? pattern) => string.IsNullOrWhiteSpace(pattern) ? None : pattern;

    private static string Roles(IEnumerable<AudioRole> roles)
    {
        var distinct = roles.Distinct().OrderBy(static role => (int)role).ToList();
        return distinct.Count == 0 ? None : string.Join(" and ", distinct);
    }
}
