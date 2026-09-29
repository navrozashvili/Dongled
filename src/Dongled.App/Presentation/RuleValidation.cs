using System.Collections.Generic;
using System.Linq;
using Dongled.Core.Configuration;

namespace Dongled.App.Presentation;

/// <summary>
/// What the rules editor warns about: a rule that, as it stands, would not do what the user
/// probably means.
/// </summary>
/// <remarks>
/// Checked live rather than on save, because edits are saved as they are made and there is no
/// save button to attach a check to.
/// </remarks>
internal static class RuleValidation
{
    /// <summary>Every problem with <paramref name="rule"/>, as one paragraph, or empty if there are none.</summary>
    /// <param name="rule">The rule being edited.</param>
    /// <param name="allRules">Every rule, including <paramref name="rule"/>.</param>
    public static string Describe(Rule rule, IReadOnlyList<Rule> allRules)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(allRules);

        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(rule.Source.Id))
        {
            problems.Add("Choose which source this rule watches.");
        }

        if (string.IsNullOrWhiteSpace(rule.Target.DeviceId) && string.IsNullOrWhiteSpace(rule.Target.NamePattern))
        {
            problems.Add("Choose which device to make the default.");
        }

        if (rule.Target.Roles.Count == 0)
        {
            problems.Add("Tick Media, Calls, or both — otherwise this rule switches nothing.");
        }

        var fallbackApplies = rule.OnDisconnect.Mode is DisconnectMode.RestorePreviousElseFallback or DisconnectMode.AlwaysFallback;

        if (fallbackApplies
            && string.IsNullOrWhiteSpace(rule.OnDisconnect.FallbackDeviceId)
            && string.IsNullOrWhiteSpace(rule.OnDisconnect.FallbackNamePattern))
        {
            problems.Add(
                rule.OnDisconnect.Mode == DisconnectMode.AlwaysFallback
                    ? "This mode always switches to a specific device, but none is chosen, so nothing will happen on disconnect."
                    : "No device is chosen for when the previous one is gone, so nothing will happen in that case.");
        }

        // Two enabled rules on one source both run, in order, so the later one decides. The engine
        // logs a warning about this; saying it here stops the user creating it by accident.
        var duplicate = rule.Enabled && allRules.Any(other =>
            other != rule
            && other.Enabled
            && !string.IsNullOrWhiteSpace(other.Source.Id)
            && string.Equals(other.Source.Id, rule.Source.Id, StringComparison.OrdinalIgnoreCase));

        if (duplicate)
        {
            problems.Add("Another switched-on rule watches the same source. Both run, in order, so the lower one decides which device ends up default.");
        }

        return string.Join(' ', problems);
    }
}
