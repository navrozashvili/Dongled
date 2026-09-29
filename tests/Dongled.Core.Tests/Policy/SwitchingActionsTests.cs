using Dongled.Core.Audio;
using Dongled.Core.Configuration;
using Dongled.Core.Policy;
using Dongled.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dongled.Core.Tests.Policy;

/// <summary>
/// What each disconnect mode does, plus the three deliberate asymmetries that look like
/// oversights.
/// </summary>
public class SwitchingActionsTests
{
    private static Rule RuleFor(
        DisconnectMode mode = DisconnectMode.RestorePreviousElseFallback,
        string? fallback = "fallback",
        params AudioRole[] roles) => new()
        {
            Id = "rule-1",
            Name = "Headset",
            Enabled = true,
            Source = new RuleSource { Id = "hyperx:cloud" },
            Target = new RuleTarget
            {
                DeviceId = "headset",
                Roles = roles.Length == 0 ? [AudioRole.Media, AudioRole.Calls] : [.. roles],
            },
            OnDisconnect = new DisconnectBehavior { Mode = mode, FallbackDeviceId = fallback },
        };

    private static (SwitchingActions Actions, FakeAudioEndpointService Endpoints, RecordingStateStore State) Create()
    {
        var endpoints = new FakeAudioEndpointService();
        endpoints.AddActive("speakers", "Speakers");
        endpoints.AddActive("headset", "Headset");
        endpoints.AddActive("fallback", "Fallback");
        endpoints.Defaults = new DefaultEndpoints("speakers", "speakers");

        var state = new RecordingStateStore();
        return (new SwitchingActions(endpoints, state, NullLogger.Instance), endpoints, state);
    }

    [Fact]
    public void Switching_captures_what_was_default_and_moves_both_roles()
    {
        var (actions, endpoints, state) = Create();

        actions.SwitchToTarget(RuleFor());

        var capture = Assert.Single(state.CaptureCalls);
        Assert.Equal("hyperx:cloud", capture.SourceId);
        Assert.Equal("speakers", capture.MultimediaId);
        Assert.Equal("speakers", capture.CommunicationsId);
        Assert.Equal("headset", capture.TargetDeviceId);

        var call = Assert.Single(endpoints.SetCalls);
        Assert.Equal("headset", call.EndpointId);
        Assert.Equal([AudioRole.Media, AudioRole.Calls], call.Roles);
    }

    [Fact]
    public void Capture_skips_a_role_already_sitting_on_the_target()
    {
        var (actions, endpoints, state) = Create();
        endpoints.Defaults = new DefaultEndpoints("headset", "speakers");

        actions.SwitchToTarget(RuleFor());

        // Remembering the target as its own previous would make the return restore to the device
        // the rule switched away from, which looks like the return doing nothing at all. The
        // state store drops it; this asserts the store is handed the truth so it can.
        var capture = Assert.Single(state.CaptureCalls);
        Assert.Equal("headset", capture.MultimediaId);
        Assert.Equal("speakers", capture.CommunicationsId);
        Assert.Equal("speakers", state.Entries["hyperx:cloud"].CommunicationsId);
        Assert.Null(state.Entries["hyperx:cloud"].MultimediaId);
    }

    [Fact]
    public void A_rule_that_only_switches_media_captures_and_moves_only_media()
    {
        var (actions, endpoints, state) = Create();

        actions.SwitchToTarget(RuleFor(roles: AudioRole.Media));

        var capture = Assert.Single(state.CaptureCalls);
        Assert.Equal("speakers", capture.MultimediaId);
        Assert.Null(capture.CommunicationsId);
        Assert.Equal([AudioRole.Media], Assert.Single(endpoints.SetCalls).Roles);
    }

    [Fact]
    public void A_rule_with_no_target_switches_nothing()
    {
        var (actions, endpoints, state) = Create();
        var rule = RuleFor();
        rule.Target.DeviceId = "   ";

        actions.SwitchToTarget(rule);

        Assert.Empty(endpoints.SetCalls);
        Assert.Empty(state.CaptureCalls);
    }

    [Fact]
    public void A_rule_with_no_roles_switches_nothing()
    {
        var (actions, endpoints, state) = Create();
        var rule = RuleFor();
        rule.Target.Roles = [];

        actions.SwitchToTarget(rule);

        Assert.Empty(endpoints.SetCalls);
        Assert.Empty(state.CaptureCalls);
    }

    [Fact]
    public void A_failed_capture_does_not_stop_the_switch()
    {
        var (actions, endpoints, state) = Create();
        state.ThrowOnCapture = true;

        actions.SwitchToTarget(RuleFor());

        // The user asked for the switch. Failing to record where to come back to is worth a log
        // line, not worth refusing what they asked for.
        Assert.Single(endpoints.SetCalls);
    }

    [Fact]
    public void Do_nothing_does_nothing()
    {
        var (actions, endpoints, _) = Create();

        actions.ApplyDisconnect(RuleFor(DisconnectMode.DoNothing));

        Assert.Empty(endpoints.SetCalls);
    }

    [Fact]
    public void Always_fallback_switches_to_the_fallback_device()
    {
        var (actions, endpoints, _) = Create();
        endpoints.Defaults = new DefaultEndpoints("headset", "headset");

        actions.ApplyDisconnect(RuleFor(DisconnectMode.AlwaysFallback));

        var call = Assert.Single(endpoints.SetCalls);
        Assert.Equal("fallback", call.EndpointId);
        Assert.Equal([AudioRole.Media, AudioRole.Calls], call.Roles);
    }

    [Fact]
    public void Always_fallback_with_no_fallback_configured_does_nothing()
    {
        var (actions, endpoints, _) = Create();

        actions.ApplyDisconnect(RuleFor(DisconnectMode.AlwaysFallback, fallback: null));

        Assert.Empty(endpoints.SetCalls);
    }

    [Fact]
    public void Restore_previous_puts_each_role_back_where_it_was()
    {
        var (actions, endpoints, state) = Create();
        endpoints.AddActive("headphones", "Headphones");
        state.Entries["hyperx:cloud"] = new PreviousDefault("speakers", "headphones", DateTimeOffset.UnixEpoch);
        endpoints.Defaults = new DefaultEndpoints("headset", "headset");

        actions.ApplyDisconnect(RuleFor(DisconnectMode.RestorePrevious));

        // Per role, one call each, because the two roles can have come from different devices.
        Assert.Equal(2, endpoints.SetCalls.Count);
        Assert.Equal("speakers", endpoints.SetCalls[0].EndpointId);
        Assert.Equal([AudioRole.Media], endpoints.SetCalls[0].Roles);
        Assert.Equal("headphones", endpoints.SetCalls[1].EndpointId);
        Assert.Equal([AudioRole.Calls], endpoints.SetCalls[1].Roles);
    }

    [Fact]
    public void Restoring_previous_leaves_a_role_alone_when_its_previous_device_is_gone()
    {
        var (actions, endpoints, state) = Create();
        state.Entries["hyperx:cloud"] = new PreviousDefault("speakers", "vanished", DateTimeOffset.UnixEpoch);
        endpoints.Defaults = new DefaultEndpoints("headset", "headset");

        actions.ApplyDisconnect(RuleFor(DisconnectMode.RestorePrevious));

        // Restoring a previous device is per role and deliberately leaves an
        // unavailable role where it is, rather than forcing it somewhere the user never chose.
        var call = Assert.Single(endpoints.SetCalls);
        Assert.Equal("speakers", call.EndpointId);
        Assert.Equal([AudioRole.Media], call.Roles);
        Assert.Equal("headset", endpoints.GetDefaults().CommunicationsId);
    }

    [Fact]
    public void A_previous_device_that_exists_but_is_unplugged_does_not_count()
    {
        var (actions, endpoints, state) = Create();
        endpoints.AddInactive("docked", "Docked speakers");
        state.Entries["hyperx:cloud"] = new PreviousDefault("docked", "docked", DateTimeOffset.UnixEpoch);

        actions.ApplyDisconnect(RuleFor(DisconnectMode.RestorePrevious));

        // A previous device counts only if it is present and active. Handing a role to an
        // unplugged endpoint produces silence.
        Assert.Empty(endpoints.SetCalls);
    }

    [Fact]
    public void Restore_previous_with_nothing_recorded_does_nothing()
    {
        var (actions, endpoints, _) = Create();

        actions.ApplyDisconnect(RuleFor(DisconnectMode.RestorePrevious));

        Assert.Empty(endpoints.SetCalls);
    }

    [Fact]
    public void Restore_previous_else_fallback_falls_back_when_the_previous_device_is_gone()
    {
        var (actions, endpoints, state) = Create();
        state.Entries["hyperx:cloud"] = new PreviousDefault("vanished", "vanished", DateTimeOffset.UnixEpoch);

        actions.ApplyDisconnect(RuleFor(DisconnectMode.RestorePreviousElseFallback));

        var call = Assert.Single(endpoints.SetCalls);
        Assert.Equal("fallback", call.EndpointId);
    }

    [Fact]
    public void Restore_previous_else_fallback_prefers_the_previous_device()
    {
        var (actions, endpoints, state) = Create();
        state.Entries["hyperx:cloud"] = new PreviousDefault("speakers", "speakers", DateTimeOffset.UnixEpoch);

        actions.ApplyDisconnect(RuleFor(DisconnectMode.RestorePreviousElseFallback));

        Assert.All(endpoints.SetCalls, call => Assert.Equal("speakers", call.EndpointId));
    }

    [Fact]
    public void A_previous_device_that_is_now_the_rules_own_target_falls_back_instead()
    {
        var (actions, endpoints, state) = Create();
        state.Entries["hyperx:cloud"] = new PreviousDefault("headset", "headset", DateTimeOffset.UnixEpoch);

        actions.ApplyDisconnect(RuleFor(DisconnectMode.RestorePreviousElseFallback));

        // The state store refuses to write the target as its own previous, but a rule re-pointed
        // at the device that was default when the capture was taken makes an already-written entry
        // become exactly that. Restoring it hands the roles straight back to the device the rule
        // switched away from, which looks like the return doing nothing at all.
        var call = Assert.Single(endpoints.SetCalls);
        Assert.Equal("fallback", call.EndpointId);
        Assert.Equal([AudioRole.Media, AudioRole.Calls], call.Roles);
    }

    [Fact]
    public void A_previous_device_that_is_now_the_rules_own_target_is_not_restored_without_a_fallback()
    {
        var (actions, endpoints, state) = Create();
        state.Entries["hyperx:cloud"] = new PreviousDefault("headset", "headset", DateTimeOffset.UnixEpoch);

        actions.ApplyDisconnect(RuleFor(DisconnectMode.RestorePrevious));

        // This mode has nowhere else to go, so the stale entry costs the user nothing rather than
        // switching them to the device they just disconnected from.
        Assert.Empty(endpoints.SetCalls);
    }

    [Fact]
    public void One_role_recorded_as_the_target_does_not_cost_the_other_role_its_return()
    {
        var (actions, endpoints, state) = Create();
        state.Entries["hyperx:cloud"] = new PreviousDefault("headset", "speakers", DateTimeOffset.UnixEpoch);

        actions.ApplyDisconnect(RuleFor(DisconnectMode.RestorePreviousElseFallback));

        // Discarding the stale role is per role, like every other reason a previous device does not
        // count. One unusable role must not turn a return that still has somewhere to go into a
        // whole-rule fallback.
        var call = Assert.Single(endpoints.SetCalls);
        Assert.Equal("speakers", call.EndpointId);
        Assert.Equal([AudioRole.Calls], call.Roles);
    }

    [Fact]
    public void A_fallback_moves_both_roles_even_for_a_rule_that_only_switches_one()
    {
        var (actions, endpoints, state) = Create();
        state.Entries["hyperx:cloud"] = new PreviousDefault("vanished", null, DateTimeOffset.UnixEpoch);

        actions.ApplyDisconnect(RuleFor(DisconnectMode.RestorePreviousElseFallback, "fallback", AudioRole.Media));

        // A deliberate asymmetry: restoring a previous device is per role, a fallback sets both. It looks like a bug and is not one.
        var call = Assert.Single(endpoints.SetCalls);
        Assert.Equal("fallback", call.EndpointId);
        Assert.Equal([AudioRole.Media, AudioRole.Calls], call.Roles);
    }

    [Fact]
    public void A_disconnect_mode_no_member_declares_behaves_like_the_default_mode()
    {
        var (actions, endpoints, state) = Create();
        state.Entries["hyperx:cloud"] = new PreviousDefault("speakers", "speakers", DateTimeOffset.UnixEpoch);
        var rule = RuleFor();
        rule.OnDisconnect.Mode = (DisconnectMode)77;

        actions.ApplyDisconnect(rule);

        // The configuration store repairs this on read, so reaching here means a rule built in
        // memory. Behaving like the default mode beats throwing on the switching path.
        Assert.All(endpoints.SetCalls, call => Assert.Equal("speakers", call.EndpointId));
        Assert.NotEmpty(endpoints.SetCalls);
    }

    [Fact]
    public void A_dead_audio_service_leaves_the_user_where_they_are_rather_than_forcing_a_fallback()
    {
        var (actions, endpoints, state) = Create();
        state.Entries["hyperx:cloud"] = new PreviousDefault("speakers", "speakers", DateTimeOffset.UnixEpoch);
        endpoints.ThrowOnEnumerate = true;

        actions.ApplyDisconnect(RuleFor(DisconnectMode.RestorePreviousElseFallback));

        // Treating "could not ask" as "the previous device is gone" would switch the user to the
        // fallback because of a transient COM failure.
        Assert.Empty(endpoints.SetCalls);
    }

    [Fact]
    public void A_target_pattern_switches_to_the_device_it_matches_even_when_the_stored_id_is_stale()
    {
        var (actions, endpoints, state) = Create();
        var rule = RuleFor();
        rule.Target.DeviceId = "reissued-by-windows";
        rule.Target.NamePattern = "^Headset$";

        actions.SwitchToTarget(rule);

        Assert.Equal("headset", Assert.Single(endpoints.SetCalls).EndpointId);
        Assert.Equal("headset", Assert.Single(state.CaptureCalls).TargetDeviceId);
    }

    [Fact]
    public void A_target_pattern_is_enough_without_any_stored_id()
    {
        var (actions, endpoints, _) = Create();
        var rule = RuleFor();
        rule.Target.DeviceId = string.Empty;
        rule.Target.NamePattern = "headset";

        actions.SwitchToTarget(rule);

        Assert.Equal("headset", Assert.Single(endpoints.SetCalls).EndpointId);
    }

    [Fact]
    public void A_target_pattern_that_matches_several_devices_keeps_the_one_the_rule_used_last()
    {
        var (actions, endpoints, _) = Create();
        endpoints.AddActive("headset-2", "Headset 2");
        var rule = RuleFor();
        rule.Target.DeviceId = "headset-2";
        rule.Target.NamePattern = "^Headset";

        actions.SwitchToTarget(rule);

        Assert.Equal("headset-2", Assert.Single(endpoints.SetCalls).EndpointId);
    }

    [Theory]
    [InlineData("^Nothing connected is called this$")]
    [InlineData("(unclosed")]
    public void A_target_pattern_that_matches_nothing_switches_nothing(string pattern)
    {
        var (actions, endpoints, state) = Create();
        var rule = RuleFor();
        rule.Target.NamePattern = pattern;

        actions.SwitchToTarget(rule);

        // Not the stored identifier either. The user said "whatever is called this", and falling
        // back to an identifier they can no longer see in the editor would switch to a device they
        // did not ask for.
        Assert.Empty(endpoints.SetCalls);
        Assert.Empty(state.CaptureCalls);
    }

    [Fact]
    public void A_target_pattern_ignores_a_matching_device_that_is_not_connected()
    {
        var (actions, endpoints, _) = Create();
        endpoints.AddInactive("old-headset", "Headset (old)");
        var rule = RuleFor();
        rule.Target.DeviceId = "old-headset";
        rule.Target.NamePattern = "^Headset";

        actions.SwitchToTarget(rule);

        Assert.Equal("headset", Assert.Single(endpoints.SetCalls).EndpointId);
    }

    [Fact]
    public void A_fallback_pattern_switches_to_the_device_it_matches()
    {
        var (actions, endpoints, _) = Create();
        var rule = RuleFor(DisconnectMode.AlwaysFallback, fallback: "reissued-by-windows");
        rule.OnDisconnect.FallbackNamePattern = "^fall";

        actions.ApplyDisconnect(rule);

        var call = Assert.Single(endpoints.SetCalls);
        Assert.Equal("fallback", call.EndpointId);
        Assert.Equal([AudioRole.Media, AudioRole.Calls], call.Roles);
    }

    [Fact]
    public void A_fallback_pattern_is_enough_without_any_stored_id()
    {
        var (actions, endpoints, _) = Create();
        var rule = RuleFor(DisconnectMode.AlwaysFallback, fallback: null);
        rule.OnDisconnect.FallbackNamePattern = "fallback";

        actions.ApplyDisconnect(rule);

        Assert.Equal("fallback", Assert.Single(endpoints.SetCalls).EndpointId);
    }

    [Fact]
    public void A_fallback_pattern_that_matches_nothing_switches_nothing()
    {
        var (actions, endpoints, _) = Create();
        var rule = RuleFor(DisconnectMode.AlwaysFallback);
        rule.OnDisconnect.FallbackNamePattern = "^Nothing connected is called this$";

        actions.ApplyDisconnect(rule);

        Assert.Empty(endpoints.SetCalls);
    }

    [Fact]
    public void A_previous_device_that_matches_the_target_pattern_counts_as_the_rules_own_target()
    {
        var (actions, endpoints, state) = Create();
        endpoints.AddActive("headset-2", "Headset 2");
        state.Entries["hyperx:cloud"] = new PreviousDefault("headset-2", "headset-2", DateTimeOffset.UnixEpoch);
        var rule = RuleFor(DisconnectMode.RestorePreviousElseFallback);
        rule.Target.NamePattern = "^Headset";

        actions.ApplyDisconnect(rule);

        // Any device the pattern names is the rule's target, not only the one it resolved to last,
        // so it is not somewhere to come back to.
        Assert.Equal("fallback", Assert.Single(endpoints.SetCalls).EndpointId);
    }
}
