using System.Collections.Generic;
using System.Linq;
using Dongled.Abstractions;
using Dongled.Core.Audio;
using Dongled.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Policy;

/// <summary>
/// Brings the default endpoints into line with what is actually connected, once at startup.
/// </summary>
/// <remarks>
/// <para>
/// Runs on the switching engine's single consumer, after presence resolution finishes. By then
/// ordinary switching has already been live for however long resolution took, so the
/// <see cref="Presence.Present"/> rows are usually no-ops — the transition to
/// <see cref="Presence.Present"/> already caused the switch. The rows that earn this pass are
/// the absent ones: a source that was never connected produces no transition, so nothing else
/// would ever notice that the user is sitting on a device that is switched off.
/// </para>
/// <para>
/// The current default is read again for every rule. Reading it once before the loop would make
/// later rules compare against a default an earlier rule had already changed.
/// </para>
/// </remarks>
internal sealed class StartupReconciler
{
    private readonly IAudioEndpointService _endpoints;
    private readonly SwitchingActions _actions;
    private readonly TimeSpan _presenceResolutionCeiling;
    private readonly ILogger _logger;

    /// <param name="endpoints">Windows audio, or a fake standing in for it.</param>
    /// <param name="actions">The endpoint changes a rule can cause.</param>
    /// <param name="presenceResolutionCeiling">
    /// How long the engine waited for presences before giving up. Passed in rather than reached
    /// for, so this type does not depend on the engine that owns it, and used only to name the
    /// figure in the log line for a source whose presence never resolved.
    /// </param>
    /// <param name="logger">Where decisions are reported.</param>
    internal StartupReconciler(
        IAudioEndpointService endpoints,
        SwitchingActions actions,
        TimeSpan presenceResolutionCeiling,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(logger);

        _endpoints = endpoints;
        _actions = actions;
        _presenceResolutionCeiling = presenceResolutionCeiling;
        _logger = logger;
    }

    /// <summary>
    /// Reconcile every enabled rule: a present source whose target does not hold its roles is
    /// switched to; an absent or unresolved source whose target holds any of its roles gets its
    /// disconnect behaviour; anything else is left alone.
    /// </summary>
    /// <param name="rules">The current rules, in the order the user arranged them.</param>
    /// <param name="presenceOf">What is known about a source's presence.</param>
    /// <param name="hasPendingReturn">
    /// Whether a source is already waiting out a stabilization delay, in which case its rules are
    /// left to the scheduler.
    /// </param>
    internal void Reconcile(
        IReadOnlyList<Rule> rules,
        Func<string, Presence> presenceOf,
        Func<string, bool> hasPendingReturn)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(presenceOf);
        ArgumentNullException.ThrowIfNull(hasPendingReturn);

        foreach (var rule in rules)
        {
            if (!rule.Enabled
                || string.IsNullOrWhiteSpace(rule.Source.Id)
                || !RuleDevices.HasTarget(rule))
            {
                continue;
            }

            try
            {
                ReconcileOne(rule, presenceOf(rule.Source.Id), hasPendingReturn);
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                // Windows audio has gone away. Every remaining rule would fail the same way, so
                // stop rather than log the same failure once per rule.
                _logger.LogError(
                    ex,
                    "Windows audio could not be reached while applying rules at startup. Reconciliation stopped after rule {RuleName}.",
                    rule.Name);
                return;
            }
            catch (Exception ex)
            {
                // One rule failing must not cost the others theirs.
                _logger.LogError(ex, "Applying rule {RuleName} at startup failed.", rule.Name);
            }
        }
    }

    private static string? Holder(DefaultEndpoints defaults, AudioRole role) =>
        role == AudioRole.Media ? defaults.MultimediaId : defaults.CommunicationsId;

    /// <summary>
    /// Whether the rule's target holds a role. For a name pattern that is any device the pattern
    /// names, so a rule is not re-applied just because Windows reissued the endpoint's identifier
    /// or a second matching device holds the role.
    /// </summary>
    private static bool Holds(
        DefaultEndpoints defaults,
        AudioRole role,
        Rule rule,
        IReadOnlyList<AudioEndpoint> endpoints) =>
        RuleDevices.IsTarget(rule, Holder(defaults, role), endpoints);

    private void ReconcileOne(Rule rule, Presence presence, Func<string, bool> hasPendingReturn)
    {
        var roles = rule.Target.Roles.Where(Enum.IsDefined).Distinct().ToArray();
        if (roles.Length == 0)
        {
            return;
        }

        // Read again per rule; an earlier rule may have just changed it.
        var defaults = _endpoints.GetDefaults();

        // Names are needed only to recognise a pattern's devices, so a rule without one costs no
        // extra enumeration.
        IReadOnlyList<AudioEndpoint> endpoints = RuleDevices.HasPattern(rule.Target.NamePattern)
            ? _endpoints.Enumerate()
            : [];

        if (presence == Presence.Present)
        {
            // "Already the target" means every role the rule affects, so a rule whose Calls role
            // has drifted elsewhere still gets put right.
            if (roles.All(role => Holds(defaults, role, rule, endpoints)))
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    // The device actually holding the role, which for a pattern may not be the
                    // stored identifier, and for a pattern-only rule there is no stored one.
                    _logger.LogInformation(
                        "{SourceId} is connected and {DeviceId} already holds its roles; rule {RuleName} needs no action at startup.",
                        rule.Source.Id,
                        Holder(defaults, roles[0]),
                        rule.Name);
                }

                return;
            }

            _actions.SwitchToTarget(rule);
            return;
        }

        if (presence == Presence.Unknown)
        {
            // Names the source and says the decision was made on an unresolved presence, so the
            // case is diagnosable from the log rather than looking like a spontaneous switch.
            _logger.LogWarning(
                "{SourceId} did not report whether it is connected within the {Ceiling} presence resolution ceiling. Rule {RuleName} is being applied on an unresolved presence, treating the source as absent, which is the behaviour that was deliberately kept.",
                rule.Source.Id,
                _presenceResolutionCeiling,
                rule.Name);
        }

        if (hasPendingReturn(rule.Source.Id))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "{SourceId} is already waiting out its stabilization delay, so rule {RuleName} is left to that rather than applied twice at startup.",
                    rule.Source.Id,
                    rule.Name);
            }

            return;
        }

        // For the absent rows, any affected role sitting on the target is enough: a user whose
        // Calls role is on a headset that is switched off is stuck for calls even if Media is not.
        if (!roles.Any(role => Holds(defaults, role, rule, endpoints)))
        {
            if (!_logger.IsEnabled(LogLevel.Information))
            {
                return;
            }

            if (RuleDevices.HasPattern(rule.Target.NamePattern))
            {
                _logger.LogInformation(
                    "{SourceId} is not connected and no device matching {Pattern} holds any of rule {RuleName}'s roles, so the default is left as it is.",
                    rule.Source.Id,
                    rule.Target.NamePattern,
                    rule.Name);
            }
            else
            {
                _logger.LogInformation(
                    "{SourceId} is not connected and {DeviceId} holds none of rule {RuleName}'s roles, so the default is left as it is.",
                    rule.Source.Id,
                    rule.Target.DeviceId,
                    rule.Name);
            }

            return;
        }

        _actions.ApplyDisconnect(rule);
    }
}
