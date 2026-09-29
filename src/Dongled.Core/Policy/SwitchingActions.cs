using System.Collections.Generic;
using System.Linq;
using Dongled.Core.Audio;
using Dongled.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Policy;

/// <summary>
/// The endpoint changes a rule can cause. Called only from the switching engine's single
/// consumer, so nothing here needs synchronising.
/// </summary>
/// <remarks>
/// Three asymmetries are deliberate: capturing a previous device skips a role already sitting on
/// the target; restoring a previous device is per role and leaves an unavailable role alone; and a
/// fallback sets both roles regardless of which roles the rule switches. Each is marked at the
/// code that implements it, because each looks like an oversight.
/// </remarks>
internal sealed class SwitchingActions
{
    // CA1861: an inline array literal as an argument allocates on every call, so the three role
    // combinations are hoisted.
    private static readonly AudioRole[] MediaOnly = [AudioRole.Media];
    private static readonly AudioRole[] CallsOnly = [AudioRole.Calls];
    private static readonly AudioRole[] BothRoles = [AudioRole.Media, AudioRole.Calls];

    private readonly IAudioEndpointService _endpoints;
    private readonly IStateStore _state;
    private readonly ILogger _logger;

    internal SwitchingActions(IAudioEndpointService endpoints, IStateStore state, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(logger);

        _endpoints = endpoints;
        _state = state;
        _logger = logger;
    }

    /// <summary>Record what was default and give the rule's roles to its target.</summary>
    internal void SwitchToTarget(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (!RuleDevices.HasTarget(rule))
        {
            _logger.LogWarning(
                "Rule {RuleName} names no device to switch to, so nothing happens when {SourceId} connects.",
                rule.Name,
                rule.Source.Id);
            return;
        }

        var roles = Roles(rule);
        if (roles.Length == 0)
        {
            _logger.LogWarning(
                "Rule {RuleName} switches no roles, so nothing happens when {SourceId} connects.",
                rule.Name,
                rule.Source.Id);
            return;
        }

        var target = ResolveDevice(rule, rule.Target.NamePattern, rule.Target.DeviceId);
        if (target is null)
        {
            return;
        }

        var defaults = _endpoints.GetDefaults();

        // Only the roles this rule affects are captured.
        var multimedia = roles.Contains(AudioRole.Media) ? defaults.MultimediaId : null;
        var communications = roles.Contains(AudioRole.Calls) ? defaults.CommunicationsId : null;

        try
        {
            // Deliberate asymmetry: a role already sitting on the target is not remembered
            // as its own previous. The state store drops it, and drops the whole entry if that
            // leaves nothing worth recording.
            _state.CapturePrevious(rule.Source.Id, multimedia, communications, target);
        }
        catch (Exception ex)
        {
            // The user asked for this switch. Failing to record where to come back to costs one
            // forgotten return, which the next capture repairs; refusing the switch would cost
            // them the thing they configured.
            _logger.LogError(
                ex,
                "Could not record what was default before switching {SourceId} to {DeviceId}. The switch still happens; its return may not.",
                rule.Source.Id,
                target);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "{SourceId} connected: giving {Roles} to {DeviceId} for rule {RuleName}.",
                rule.Source.Id,
                string.Join(" and ", roles),
                target,
                rule.Name);
        }

        _endpoints.SetDefault(target, roles);
    }

    /// <summary>Apply the rule's disconnect behaviour.</summary>
    internal void ApplyDisconnect(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        switch (rule.OnDisconnect.Mode)
        {
            case DisconnectMode.DoNothing:
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "{SourceId} disconnected and rule {RuleName} says to leave the default alone.",
                        rule.Source.Id,
                        rule.Name);
                }

                return;

            case DisconnectMode.AlwaysFallback:
                SwitchToFallback(rule);
                return;

            case DisconnectMode.RestorePrevious:
                RestorePrevious(rule, allowFallback: false);
                return;

            case DisconnectMode.RestorePreviousElseFallback:
            default:
                // A mode built in memory that names no declared member behaves like the ordinary
                // case rather than throwing on the
                // switching path. Loaded configuration cannot reach here: the store repairs it.
                RestorePrevious(rule, allowFallback: true);
                return;
        }
    }

    private static AudioRole[] Roles(Rule rule) => rule.Target.Roles
        .Where(Enum.IsDefined)
        .Distinct()
        .OrderBy(role => (int)role)
        .ToArray();

    private static AudioRole[] RoleArray(AudioRole role) =>
        role == AudioRole.Media ? MediaOnly : CallsOnly;

    private static bool Available(string? deviceId, HashSet<string> active) =>
        !string.IsNullOrWhiteSpace(deviceId) && active.Contains(deviceId);

    /// <summary>
    /// The device to give one role back to, or null if what was recorded is no use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A previous device counts only if it is recorded, present, and active: handing a role to an
    /// unplugged endpoint produces silence.
    /// </para>
    /// <para>
    /// And only if it is not the rule's own target. That check is the read-side twin of the one in
    /// <see cref="StateStore.CapturePrevious"/>, which refuses to write the target as its own
    /// previous. The write-side guard alone is not enough, because it holds only for as long as the
    /// rule keeps the target it had when the capture was taken: point a rule at the device that was
    /// default at that moment and the entry already on disk becomes exactly the shape the store
    /// would have refused. Restoring it hands the roles straight back to the device the rule
    /// switched away from — which looks like the return doing nothing, and, worse, counts as a
    /// successful restore, so <see cref="DisconnectMode.RestorePreviousElseFallback"/> never
    /// reaches its fallback.
    /// </para>
    /// <para>
    /// Discarded per role rather than for the whole entry, like every other reason a previous
    /// device does not count, so one stale role does not cost the other one its return.
    /// </para>
    /// </remarks>
    private string? UsablePrevious(
        string? deviceId,
        Rule rule,
        HashSet<string> active,
        IReadOnlyList<AudioEndpoint> endpoints,
        AudioRole role)
    {
        if (!Available(deviceId, active))
        {
            return null;
        }

        if (!RuleDevices.IsTarget(rule, deviceId, endpoints))
        {
            return deviceId;
        }

        // Logged because the symptom this causes is silent: the roles are set, the log says they
        // were returned, and the only clue that anything is wrong is that the device did not
        // change. Information rather than Warning — it is a stale entry the next connect repairs,
        // not a failure.
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "{DeviceId} is recorded as {SourceId}'s previous {Role} default but is rule {RuleName}'s own target, so it is not somewhere to come back to and is ignored.",
                deviceId,
                rule.Source.Id,
                role,
                rule.Name);
        }

        return null;
    }

    private void RestorePrevious(Rule rule, bool allowFallback)
    {
        var previous = _state.GetPrevious(rule.Source.Id);
        var roles = Roles(rule);

        IReadOnlyList<AudioEndpoint> endpoints;
        HashSet<string> active;
        try
        {
            endpoints = _endpoints.Enumerate();
            active = endpoints
                .Where(endpoint => endpoint.IsActive)
                .Select(endpoint => endpoint.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // Broad on purpose. Not being able to ask is not the same as the previous device being
            // gone, and reading it as gone would fall back to a device the user did not choose
            // because of a transient failure. Nothing happens and they stay where they are.
            _logger.LogError(
                ex,
                "Could not list playback endpoints, so the return for {SourceId} was skipped. The default device is left as it is.",
                rule.Source.Id);
            return;
        }

        var multimedia = roles.Contains(AudioRole.Media)
            ? UsablePrevious(previous?.MultimediaId, rule, active, endpoints, AudioRole.Media)
            : null;
        var communications = roles.Contains(AudioRole.Calls)
            ? UsablePrevious(previous?.CommunicationsId, rule, active, endpoints, AudioRole.Calls)
            : null;

        if (multimedia is not null || communications is not null)
        {
            // Deliberate asymmetry: this is per role, and a role whose previous device is
            // unavailable is deliberately left where it is rather than forced somewhere.
            if (multimedia is not null)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "{SourceId} disconnected: returning the Media role to {DeviceId}.",
                        rule.Source.Id,
                        multimedia);
                }

                _endpoints.SetDefault(multimedia, RoleArray(AudioRole.Media));
            }

            if (communications is not null)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "{SourceId} disconnected: returning the Calls role to {DeviceId}.",
                        rule.Source.Id,
                        communications);
                }

                _endpoints.SetDefault(communications, RoleArray(AudioRole.Calls));
            }

            return;
        }

        if (allowFallback)
        {
            SwitchToFallback(rule);
            return;
        }

        _logger.LogWarning(
            "{SourceId} disconnected, but nothing it was switched away from is available and rule {RuleName} has no fallback, so the default is left as it is.",
            rule.Source.Id,
            rule.Name);
    }

    private void SwitchToFallback(Rule rule)
    {
        var pattern = rule.OnDisconnect.FallbackNamePattern;
        if (!RuleDevices.HasPattern(pattern) && string.IsNullOrWhiteSpace(rule.OnDisconnect.FallbackDeviceId))
        {
            _logger.LogWarning(
                "Rule {RuleName} is set to use a fallback device but names none, so nothing happens when {SourceId} disconnects.",
                rule.Name,
                rule.Source.Id);
            return;
        }

        var fallback = ResolveDevice(rule, pattern, rule.OnDisconnect.FallbackDeviceId);
        if (fallback is null)
        {
            return;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "{SourceId} disconnected: switching to the fallback {DeviceId} for rule {RuleName}.",
                rule.Source.Id,
                fallback,
                rule.Name);
        }

        // Deliberate asymmetry: a fallback sets both roles regardless of which roles the rule
        // switches. It reads like a bug, but it is the intended behaviour.
        _endpoints.SetDefault(fallback, BothRoles);
    }

    /// <summary>
    /// The endpoint a rule's device field means right now: the stored identifier when there is no
    /// pattern, and otherwise the connected device the pattern resolves to. Null, having said why,
    /// when a pattern resolves to nothing.
    /// </summary>
    /// <remarks>
    /// A pattern that matches nothing does not fall back to the stored identifier. The user said
    /// "whatever is called this", and switching to an identifier the editor no longer shows them
    /// would be switching to a device they did not ask for.
    /// </remarks>
    /// <exception cref="Exception">Windows audio could not be listed. See <see cref="IAudioEndpointService.Enumerate"/>.</exception>
    private string? ResolveDevice(Rule rule, string? pattern, string? storedId)
    {
        if (!RuleDevices.HasPattern(pattern))
        {
            return string.IsNullOrWhiteSpace(storedId) ? null : storedId;
        }

        if (!DeviceNameMatcher.TryValidate(pattern!, out var error))
        {
            _logger.LogWarning(
                "Rule {RuleName} names its device by the pattern {Pattern}, which cannot be used ({Error}), so nothing is switched.",
                rule.Name,
                pattern,
                error);
            return null;
        }

        var candidates = DeviceNameMatcher.Candidates(pattern!, _endpoints.Enumerate());
        var chosen = DeviceNameMatcher.Pick(candidates, storedId);

        if (chosen is null)
        {
            _logger.LogWarning(
                "No connected playback device matches {Pattern} for rule {RuleName}, so nothing is switched.",
                pattern,
                rule.Name);
            return null;
        }

        // Several matches is not an error, but which one won must be diagnosable from the log.
        if (candidates.Count > 1 && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "{Pattern} matches {Count} connected devices ({Candidates}) for rule {RuleName}; using {DeviceId}.",
                pattern,
                candidates.Count,
                string.Join(", ", candidates.Select(static endpoint => endpoint.DisplayName)),
                rule.Name,
                chosen.Id);
        }

        return chosen.Id;
    }
}
