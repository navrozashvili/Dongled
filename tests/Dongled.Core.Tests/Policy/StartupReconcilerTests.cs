using System.Collections.Generic;
using Dongled.Abstractions;
using Dongled.Core.Audio;
using Dongled.Core.Configuration;
using Dongled.Core.Policy;
using Dongled.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dongled.Core.Tests.Policy;

/// <summary>
/// The startup decision for each combination of source presence and current default, one test
/// per case.
/// </summary>
public class StartupReconcilerTests
{
    private static Rule RuleFor(string sourceId, string target, string? fallback = "fallback") => new()
    {
        Id = sourceId,
        Name = sourceId,
        Enabled = true,
        Source = new RuleSource { Id = sourceId },
        Target = new RuleTarget { DeviceId = target, Roles = [AudioRole.Media, AudioRole.Calls] },
        OnDisconnect = new DisconnectBehavior
        {
            Mode = DisconnectMode.RestorePreviousElseFallback,
            FallbackDeviceId = fallback,
        },
    };

    private static (StartupReconciler Reconciler, FakeAudioEndpointService Endpoints, RecordingStateStore State) Create()
    {
        var endpoints = new FakeAudioEndpointService();
        endpoints.AddActive("speakers", "Speakers");
        endpoints.AddActive("headset", "Headset");
        endpoints.AddActive("mic-headset", "Mic headset");
        endpoints.AddActive("fallback", "Fallback");
        endpoints.Defaults = new DefaultEndpoints("speakers", "speakers");

        var state = new RecordingStateStore();
        var actions = new SwitchingActions(endpoints, state, NullLogger.Instance);
        var reconciler = new StartupReconciler(
            endpoints,
            actions,
            TimeSpan.FromSeconds(5),
            NullLogger.Instance);

        return (reconciler, endpoints, state);
    }

    private static Func<string, Presence> Presences(Dictionary<string, Presence> map) =>
        sourceId => map.GetValueOrDefault(sourceId, Presence.Unknown);

    private static bool NothingPending(string sourceId) => false;

    [Fact]
    public void Present_and_the_default_is_elsewhere_switches_to_the_target()
    {
        var (reconciler, endpoints, _) = Create();

        reconciler.Reconcile(
            [RuleFor("headset-source", "headset")],
            Presences(new() { ["headset-source"] = Presence.Present }),
            NothingPending);

        Assert.Equal("headset", Assert.Single(endpoints.SetCalls).EndpointId);
    }

    [Fact]
    public void Present_and_already_at_the_target_does_nothing()
    {
        var (reconciler, endpoints, _) = Create();
        endpoints.Defaults = new DefaultEndpoints("headset", "headset");

        reconciler.Reconcile(
            [RuleFor("headset-source", "headset")],
            Presences(new() { ["headset-source"] = Presence.Present }),
            NothingPending);

        Assert.Empty(endpoints.SetCalls);
    }

    [Fact]
    public void Present_with_only_one_role_on_the_target_switches_so_the_other_role_catches_up()
    {
        var (reconciler, endpoints, _) = Create();
        endpoints.Defaults = new DefaultEndpoints("headset", "speakers");

        // "Already the target" has to mean every role the rule affects. Reading it as "any role"
        // would leave the Calls role on a device the rule was configured to move.
        reconciler.Reconcile(
            [RuleFor("headset-source", "headset")],
            Presences(new() { ["headset-source"] = Presence.Present }),
            NothingPending);

        Assert.Equal("headset", Assert.Single(endpoints.SetCalls).EndpointId);
    }

    [Fact]
    public void Absent_and_the_default_is_the_target_applies_the_disconnect_behaviour()
    {
        var (reconciler, endpoints, state) = Create();
        endpoints.Defaults = new DefaultEndpoints("headset", "headset");
        state.Entries["headset-source"] = new PreviousDefault("speakers", "speakers", DateTimeOffset.UnixEpoch);

        reconciler.Reconcile(
            [RuleFor("headset-source", "headset")],
            Presences(new() { ["headset-source"] = Presence.Absent }),
            NothingPending);

        Assert.NotEmpty(endpoints.SetCalls);
        Assert.All(endpoints.SetCalls, call => Assert.Equal("speakers", call.EndpointId));
    }

    [Fact]
    public void Absent_and_the_default_is_something_else_does_nothing()
    {
        var (reconciler, endpoints, state) = Create();
        state.Entries["headset-source"] = new PreviousDefault("speakers", "speakers", DateTimeOffset.UnixEpoch);

        // The user is not sitting on a dead device, so there is nothing to rescue them from.
        reconciler.Reconcile(
            [RuleFor("headset-source", "headset")],
            Presences(new() { ["headset-source"] = Presence.Absent }),
            NothingPending);

        Assert.Empty(endpoints.SetCalls);
    }

    [Fact]
    public void Absent_with_only_one_role_stuck_on_the_target_still_rescues_that_role()
    {
        var (reconciler, endpoints, state) = Create();
        endpoints.Defaults = new DefaultEndpoints("speakers", "headset");
        state.Entries["headset-source"] = new PreviousDefault("speakers", "speakers", DateTimeOffset.UnixEpoch);

        // For the absent rows "the default is the target" means any affected role, because a user
        // whose Calls role is on a headset that is switched off is stuck for calls.
        reconciler.Reconcile(
            [RuleFor("headset-source", "headset")],
            Presences(new() { ["headset-source"] = Presence.Absent }),
            NothingPending);

        Assert.NotEmpty(endpoints.SetCalls);
    }

    [Fact]
    public void Unknown_at_the_ceiling_is_treated_as_absent()
    {
        var (reconciler, endpoints, state) = Create();
        endpoints.Defaults = new DefaultEndpoints("headset", "headset");
        state.Entries["headset-source"] = new PreviousDefault("speakers", "speakers", DateTimeOffset.UnixEpoch);

        // Pins a deliberate decision: a provider that has not finished connecting reports
        // nothing, is treated as absent, and can therefore switch away from a headset that is in
        // fact on.
        reconciler.Reconcile(
            [RuleFor("headset-source", "headset")],
            Presences([]),
            NothingPending);

        Assert.NotEmpty(endpoints.SetCalls);
        Assert.All(endpoints.SetCalls, call => Assert.Equal("speakers", call.EndpointId));
    }

    [Fact]
    public void The_current_default_is_read_again_for_every_rule()
    {
        var (reconciler, endpoints, _) = Create();

        // Reading the default once before the loop would make later rules compare against a
        // default an earlier rule had already changed.
        reconciler.Reconcile(
            [RuleFor("a", "headset"), RuleFor("b", "mic-headset")],
            Presences(new() { ["a"] = Presence.Present, ["b"] = Presence.Present }),
            NothingPending);

        Assert.Equal(2, endpoints.SetCalls.Count);
        Assert.Equal("headset", endpoints.SetCalls[0].EndpointId);
        Assert.Equal("mic-headset", endpoints.SetCalls[1].EndpointId);
        Assert.Equal("mic-headset", endpoints.GetDefaults().MultimediaId);
    }

    [Fact]
    public void A_second_rule_already_satisfied_by_the_first_takes_no_action()
    {
        var (reconciler, endpoints, _) = Create();

        reconciler.Reconcile(
            [RuleFor("a", "headset"), RuleFor("b", "headset")],
            Presences(new() { ["a"] = Presence.Present, ["b"] = Presence.Present }),
            NothingPending);

        // Without the per-rule re-read this would switch twice.
        Assert.Single(endpoints.SetCalls);
    }

    [Fact]
    public void A_source_already_waiting_out_a_stabilization_delay_is_left_to_the_scheduler()
    {
        var (reconciler, endpoints, state) = Create();
        endpoints.Defaults = new DefaultEndpoints("headset", "headset");
        state.Entries["headset-source"] = new PreviousDefault("speakers", "speakers", DateTimeOffset.UnixEpoch);

        // A source that disconnected while startup was still resolving already has its return
        // pending. Acting here too would apply the disconnect twice and throw away the flicker
        // protection the delay exists for.
        reconciler.Reconcile(
            [RuleFor("headset-source", "headset")],
            Presences(new() { ["headset-source"] = Presence.Absent }),
            _ => true);

        Assert.Empty(endpoints.SetCalls);
    }

    [Fact]
    public void A_disabled_rule_is_ignored()
    {
        var (reconciler, endpoints, _) = Create();
        var rule = RuleFor("headset-source", "headset");
        rule.Enabled = false;

        reconciler.Reconcile([rule], Presences(new() { ["headset-source"] = Presence.Present }), NothingPending);

        Assert.Empty(endpoints.SetCalls);
    }

    [Fact]
    public void A_rule_with_no_source_or_no_target_is_ignored()
    {
        var (reconciler, endpoints, _) = Create();
        var noSource = RuleFor("  ", "headset");
        var noTarget = RuleFor("headset-source", "   ");

        reconciler.Reconcile(
            [noSource, noTarget],
            Presences(new() { ["headset-source"] = Presence.Present }),
            NothingPending);

        Assert.Empty(endpoints.SetCalls);
    }

    [Fact]
    public void One_rule_failing_does_not_stop_the_rest()
    {
        var (reconciler, endpoints, state) = Create();
        state.ThrowOnCapture = true;

        reconciler.Reconcile(
            [RuleFor("a", "headset"), RuleFor("b", "mic-headset")],
            Presences(new() { ["a"] = Presence.Present, ["b"] = Presence.Present }),
            NothingPending);

        Assert.Equal(2, endpoints.SetCalls.Count);
    }

    [Fact]
    public void A_dead_audio_service_stops_reconciliation_without_throwing()
    {
        var (reconciler, endpoints, _) = Create();
        endpoints.ThrowOnGetDefaults = true;

        reconciler.Reconcile(
            [RuleFor("a", "headset")],
            Presences(new() { ["a"] = Presence.Present }),
            NothingPending);

        Assert.Empty(endpoints.SetCalls);
    }

    [Fact]
    public void A_rule_with_only_a_target_pattern_is_reconciled()
    {
        var (reconciler, endpoints, _) = Create();
        var rule = RuleFor("headset-source", string.Empty);
        rule.Target.NamePattern = "^Headset$";

        reconciler.Reconcile(
            [rule],
            Presences(new() { ["headset-source"] = Presence.Present }),
            NothingPending);

        Assert.Equal("headset", Assert.Single(endpoints.SetCalls).EndpointId);
    }

    [Fact]
    public void Present_and_a_device_matching_the_pattern_already_holds_the_roles_does_nothing()
    {
        var (reconciler, endpoints, _) = Create();
        endpoints.Defaults = new DefaultEndpoints("mic-headset", "mic-headset");
        var rule = RuleFor("headset-source", "headset");
        rule.Target.NamePattern = "headset";

        reconciler.Reconcile(
            [rule],
            Presences(new() { ["headset-source"] = Presence.Present }),
            NothingPending);

        Assert.Empty(endpoints.SetCalls);
    }

    [Fact]
    public void Absent_and_a_device_matching_the_pattern_holds_a_role_applies_the_disconnect()
    {
        var (reconciler, endpoints, _) = Create();
        endpoints.Defaults = new DefaultEndpoints("mic-headset", "speakers");
        var rule = RuleFor("headset-source", "headset");
        rule.Target.NamePattern = "headset";
        rule.OnDisconnect.Mode = DisconnectMode.AlwaysFallback;

        reconciler.Reconcile(
            [rule],
            Presences(new() { ["headset-source"] = Presence.Absent }),
            NothingPending);

        Assert.Equal("fallback", Assert.Single(endpoints.SetCalls).EndpointId);
    }
}
