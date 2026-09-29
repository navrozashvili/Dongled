using System.Diagnostics;
using Dongled.Abstractions;
using Dongled.Plugin.HyperXHid;
using Xunit;

namespace Dongled.Plugins.Tests.HyperXHid;

public sealed class HyperXHidProviderTests
{
    private const string SourceId = "hyperx:cloud-iii-s-wireless:any";

    private static readonly HyperXHidTimings Fast = new(
        EventReadTimeout: TimeSpan.FromMilliseconds(20),
        BatteryProbeTimeout: TimeSpan.FromMilliseconds(60),
        BatteryReadTimeout: TimeSpan.FromMilliseconds(20),
        BatteryPoll: TimeSpan.FromMilliseconds(30),
        NoDongleRetryDelay: TimeSpan.FromMilliseconds(20),
        SessionRetryDelay: TimeSpan.FromMilliseconds(20),
        ChargingStale: TimeSpan.FromMilliseconds(200));

    [Fact]
    public void The_metadata_is_the_published_provider_identity()
    {
        var provider = new HyperXHidProvider(new FakeHeadsetTransport(), Fast, TimeProvider.System);

        Assert.Equal("hyperx.cloud3s.hid", provider.Metadata.Id);
        Assert.Equal("HyperX Cloud III S Wireless (HID)", provider.Metadata.DisplayName);
        Assert.False(provider.Metadata.IsExperimental);
    }

    [Fact]
    public async Task Starting_publishes_the_one_source_before_it_knows_anything_about_it()
    {
        var transport = new FakeHeadsetTransport().ThenSession(answersBatteryProbe: false);
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            // Published synchronously: the picker should list the headset immediately, and the presence
            // answer follows from the watch loop.
            var descriptor = Assert.Single(Assert.Single(context.Publications));
            Assert.Equal(SourceId, descriptor.SourceId);
            Assert.Equal("HyperX Cloud III S Wireless", descriptor.DisplayName);
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task StartAsync_returns_without_waiting_for_the_watch_loop()
    {
        // The host gives StartAsync roughly ten seconds and treats a provider that has not returned as
        // failed. A provider that ran its loop inline would also block the host's queue.
        var transport = new FakeHeadsetTransport().ThenSession(answersBatteryProbe: true);
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);
        var context = new FakeProviderContext();

        var elapsed = Stopwatch.StartNew();
        await provider.StartAsync(context, TestContext.Current.CancellationToken);
        elapsed.Stop();

        try
        {
            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(1),
                $"StartAsync took {elapsed.Elapsed.TotalMilliseconds:0} ms, so it is doing the loop's work.");
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_headset_that_answers_the_battery_probe_is_present()
    {
        var transport = new FakeHeadsetTransport().ThenSession(answersBatteryProbe: true);
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.PresenceOf(SourceId) == Presence.Present,
                "the battery probe's answer to be reported as present");
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_headset_that_ignores_the_battery_probe_is_absent_rather_than_unreported()
    {
        // Reporting nothing would leave the host with no answer at all, waiting out its startup
        // deadline. Absent is the answer.
        var transport = new FakeHeadsetTransport().ThenSession(answersBatteryProbe: false);
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.PresenceOf(SourceId) == Presence.Absent,
                "a silent battery probe to be reported as absent");
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task No_dongle_is_absent_and_is_not_reported_as_a_failure()
    {
        var transport = new FakeHeadsetTransport().ThenNoDongle();
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.PresenceOf(SourceId) == Presence.Absent,
                "an absent dongle to be reported as absent");

            await Eventually.UntilAsync(
                () => transport.OpenAttempts >= 2,
                "the provider to scan again rather than giving up");

            // An absent dongle is an ordinary state, so nothing about it
            // is logged as a warning or an error.
            Assert.DoesNotContain(context.Recorded.Entries, e => e.Level >= ProviderLogLevel.Warning);
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Connection_events_are_reported_as_they_arrive()
    {
        var transport = new FakeHeadsetTransport()
            .ThenSession(answersBatteryProbe: false, Event(0x0C, 1), Event(0x0C, 0), Event(0x0A, 1));
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.PresenceHistory(SourceId).Count >= 3,
                "all three connection events to be reported");

            // The first event is consumed by the battery probe, which returns it rather than discarding
            // it: an event states the current state, where the probe only infers it. So the probe's own
            // "no reply, therefore absent" never happens here, and the sequence is the three events.
            Assert.Equal(
                [Presence.Present, Presence.Absent, Presence.Present],
                context.PresenceHistory(SourceId).Take(3));
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_report_that_is_not_a_connection_event_reports_nothing()
    {
        var transport = new FakeHeadsetTransport()
            .ThenSession(answersBatteryProbe: false, Event(0x0B, 1), Event(0x0C, 2));
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => transport.Sessions.Count == 1,
                "the session to be opened");

            // Give the loop room to mishandle the two reports if it is going to.
            await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

            Assert.Equal([Presence.Absent], context.PresenceHistory(SourceId));
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_session_that_throws_is_absent_retried_and_explained()
    {
        var transport = new FakeHeadsetTransport()
            .ThenThrows(new IOException("the dongle was unplugged"))
            .ThenSession(answersBatteryProbe: true);
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.PresenceOf(SourceId) == Presence.Present,
                "the provider to recover on its next attempt");

            Assert.Equal(Presence.Absent, context.PresenceHistory(SourceId)[0]);
            Assert.True(context.Recorded.Mentions("unplugged"), "the failure should be explained");
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Stopping_disposes_the_open_session_and_reports_nothing_further()
    {
        var transport = new FakeHeadsetTransport().ThenSession(answersBatteryProbe: true);
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);
        await Eventually.UntilAsync(
            () => context.PresenceOf(SourceId) == Presence.Present,
            "the headset to be reported present before stopping");

        var beforeStop = context.PresenceReports.Count;
        await provider.StopAsync(TestContext.Current.CancellationToken);

        // No parting Absent. A plugin cannot tell "the user disabled me" from "the host is shutting
        // down", and a disconnect return fired during shutdown would move the user's default device on
        // the way out. If the app ever wants that when a plugin is disabled, the host should
        // synthesize it, where the difference is known.
        Assert.Equal(beforeStop, context.PresenceReports.Count);
        Assert.All(transport.Sessions, s => Assert.True(s.Disposed));
    }

    [Fact]
    public async Task Stopping_a_provider_that_never_started_does_not_throw()
    {
        // ProviderRunner calls StopAsync even when StartAsync faulted, so this has to be safe.
        var provider = new HyperXHidProvider(new FakeHeadsetTransport(), Fast, TimeProvider.System);

        await provider.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Cancelling_the_hosts_token_stops_the_watch_loop()
    {
        var transport = new FakeHeadsetTransport().ThenSession(answersBatteryProbe: true);
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);
        var context = new FakeProviderContext();
        using var host = new CancellationTokenSource();

        await provider.StartAsync(context, host.Token);
        await Eventually.UntilAsync(
            () => context.PresenceOf(SourceId) == Presence.Present,
            "the headset to be reported present");

        await host.CancelAsync();

        await Eventually.UntilAsync(
            () => transport.Sessions.All(s => s.Disposed),
            "the watch loop to notice the host's cancellation and release the session");

        await provider.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Battery_capability_is_declared_as_soon_as_the_provider_starts()
    {
        // Before any dongle is seen. This is what puts a switched-off headset on the battery page.
        //
        // Asserted synchronously right after StartAsync returns, not through Eventually: the
        // declaration (HyperXHidProvider.cs, in StartAsync) runs on the caller's thread before the
        // watch loop's thread is even started, so it is already true the instant the await completes.
        // A bare FakeHeadsetTransport with no script has TryOpen() return null on the watch loop's
        // first pass, and the no-dongle branch reports the same (SourceId, null, Unknown) shape a
        // moment later on its own thread -- wrapping this in Eventually.UntilAsync would happily wait
        // for that byte-identical report instead, and the test would stay green even if the up-front
        // declaration were deleted.
        var transport = new FakeHeadsetTransport();
        var context = new FakeProviderContext();
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        Assert.NotEmpty(context.BatteryReports);
        var first = context.BatteryReports[0];
        Assert.Equal(SourceId, first.SourceId);
        Assert.Null(first.Percent);
        Assert.Equal(ChargeState.Unknown, first.Charge);

        await provider.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_battery_reply_is_reported_as_a_percentage()
    {
        var transport = new FakeHeadsetTransport();
        transport.AnswerBatteryRequestsWith(percentage: 63);
        var context = new FakeProviderContext();
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        await Eventually.UntilAsync(
            () => context.BatteryReports.Any(r => r.Percent == 63),
            "the probed percentage to be reported");

        await provider.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Charging_is_unknown_until_an_edge_arrives()
    {
        // The device limitation, asserted rather than worked around.
        var transport = new FakeHeadsetTransport();
        transport.AnswerBatteryRequestsWith(percentage: 63);
        var context = new FakeProviderContext();
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);

        await provider.StartAsync(context, TestContext.Current.CancellationToken);
        await Eventually.UntilAsync(
            () => context.BatteryReports.Any(r => r.Percent == 63),
            "the probed percentage to be reported");

        Assert.All(context.BatteryReports, r => Assert.Equal(ChargeState.Unknown, r.Charge));

        await provider.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_charging_edge_mid_session_is_reported_through_the_battery_channel()
    {
        // The probe answers first (ScriptedSession always answers the battery request on the very
        // first read once one is pending), so this 0x0A/state-1 report is left in the queue for
        // ReadEvents, not ProbePresence, to read. ReadEvents is what has to notice it, latch it into
        // the tracker, and re-report the battery reading so the level-unchanged-but-charge-changed case
        // actually reaches the host.
        var transport = new FakeHeadsetTransport()
            .ThenSession(answersBatteryProbe: true, Event(0x0A, 1));
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.BatteryReports.Any(r => r.Percent == 72 && r.Charge == ChargeState.Charging),
                "the charging edge, read after the probe already answered, to surface as a fresh battery report");
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Losing_the_dongle_repeatedly_clears_the_battery_reading()
    {
        // A headset that goes away must not leave a stale percentage on screen. The no-dongle branch
        // re-clears the reading on every retry it makes, not just once -- so counting to more than the
        // single startup declaration is what tells this apart from that declaration alone.
        var transport = new FakeHeadsetTransport().ThenNoDongle();
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.BatteryReports.Count(r => r.Percent is null && r.Charge == ChargeState.Unknown) >= 3,
                "the no-dongle branch to keep re-clearing the battery reading on every retry, not just declare it once at startup");
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_session_that_closes_clears_the_remembered_battery_state()
    {
        // The stream ending is what the dongle or headset going away looks like once a session is
        // already open (as opposed to no dongle ever answering at all). The second, plain session never
        // answers its own battery probe and never scripts any reports, so it contributes no further
        // battery reports of its own -- nothing after the close should be able to explain a later
        // (null, Unknown) reading except the disconnect handling itself.
        var transport = new FakeHeadsetTransport()
            .ThenSessionThatCloses(answersBatteryProbe: true, Event(0x0A, 1))
            .ThenSession(answersBatteryProbe: false);
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.BatteryReports.Any(r => r.Percent == 72 && r.Charge == ChargeState.Charging),
                "the first session to establish a real percentage and a charging edge before closing");

            await Eventually.UntilAsync(
                () => context.BatteryReports[^1] is { Percent: null, Charge: ChargeState.Unknown },
                "the stream closing to clear the remembered percentage and charge state");
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Reconnecting_after_a_closed_session_does_not_inherit_the_latched_charge_state()
    {
        // What was true of the headset that just went away says nothing about the next one to pair
        // with the dongle. The second session answers its own battery probe (55%, distinct from the
        // first session's 72%) but never sees a charging edge of its own, so the only way its report
        // can say anything but Unknown is if the tracker was never reset after the first session closed.
        var transport = new FakeHeadsetTransport()
            .ThenSessionThatCloses(answersBatteryProbe: true, Event(0x0A, 1))
            .AnswerBatteryRequestsWith(percentage: 55);
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.BatteryReports.Any(r => r.Percent == 55),
                "the second session to answer its own battery probe after reconnecting");

            var afterReconnect = context.BatteryReports.Last(r => r.Percent == 55);
            Assert.Equal(ChargeState.Unknown, afterReconnect.Charge);
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Reconnecting_after_a_closed_session_does_not_inherit_the_remembered_percentage()
    {
        // The second session never answers its battery probe, so ProbePresence can never legitimately
        // set _lastPercent itself -- its first event report is consumed by the probe (a physical-tag
        // report is simultaneously a presence statement, ending the probe loop) and carries no battery
        // reading, so the only battery report this session can produce comes from its second event
        // reaching ReadEvents, which reports whatever percentage the provider still remembers.
        var transport = new FakeHeadsetTransport()
            .ThenSessionThatCloses(answersBatteryProbe: true)
            .ThenSession(answersBatteryProbe: false, Event(0x0A, 1), Event(0x0A, 0));
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.BatteryReports.Any(r => r.Percent == 72),
                "the first session to report its probed percentage before disconnecting");

            await Eventually.UntilAsync(
                () => context.BatteryReports.Any(r => r.Charge == ChargeState.Discharging),
                "the second session's stop edge to surface, exposing whatever percent the provider still remembers");

            var afterReconnect = context.BatteryReports.First(r => r.Charge == ChargeState.Discharging);
            Assert.Null(afterReconnect.Percent);
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_session_that_throws_mid_read_clears_the_remembered_battery_state()
    {
        // The exception path (a session that opened fine but then failed to read) is a separate branch
        // from both "no dongle" and "the stream closed cleanly". Same shape as
        // A_session_that_closes_clears_the_remembered_battery_state, aimed at that branch instead.
        var transport = new FakeHeadsetTransport()
            .ThenSessionThatThrowsAfter(
                answersBatteryProbe: true, new IOException("the dongle was unplugged"), Event(0x0A, 1))
            .ThenSession(answersBatteryProbe: false);
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.BatteryReports.Any(r => r.Percent == 72 && r.Charge == ChargeState.Charging),
                "the first session to establish a real percentage and a charging edge before failing");

            await Eventually.UntilAsync(
                () => context.BatteryReports[^1] is { Percent: null, Charge: ChargeState.Unknown },
                "the session failing mid-read to clear the remembered percentage and charge state");
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Reconnecting_after_a_failed_session_does_not_inherit_the_latched_charge_state()
    {
        // The exception path's counterpart to
        // Reconnecting_after_a_closed_session_does_not_inherit_the_latched_charge_state. A device that
        // is not charging must not show a charging indicator inherited from its predecessor, and a
        // session that died mid-read teaches nothing about the headset that pairs with the dongle next.
        // The second session answers its own battery probe (55%, distinct from the first session's 72%)
        // but never sees a charging edge of its own, so the only way its report can say anything but
        // Unknown is if the tracker was never reset after the first session threw.
        var transport = new FakeHeadsetTransport()
            .ThenSessionThatThrowsAfter(
                answersBatteryProbe: true, new IOException("the dongle was unplugged"), Event(0x0A, 1))
            .AnswerBatteryRequestsWith(percentage: 55);
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.BatteryReports.Any(r => r.Percent == 55),
                "the second session to answer its own battery probe after the first one failed");

            var afterReconnect = context.BatteryReports.Last(r => r.Percent == 55);
            Assert.Equal(ChargeState.Unknown, afterReconnect.Charge);
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Reconnecting_after_a_failed_session_does_not_inherit_the_remembered_percentage()
    {
        // The exception path's counterpart to
        // Reconnecting_after_a_closed_session_does_not_inherit_the_remembered_percentage. The first
        // session answers its probe (72%) and then throws before scripting any edge, so the charge
        // state is Unknown on both sides of the failure and only the remembered percentage is in play.
        // The second session never answers its battery probe, so ProbePresence can never legitimately
        // set _lastPercent itself -- its first event report is consumed by the probe (a physical-tag
        // report is simultaneously a presence statement, ending the probe loop) and carries no battery
        // reading, so the only battery report this session can produce comes from its second event
        // reaching ReadEvents, which reports whatever percentage the provider still remembers.
        var transport = new FakeHeadsetTransport()
            .ThenSessionThatThrowsAfter(answersBatteryProbe: true, new IOException("the dongle was unplugged"))
            .ThenSession(answersBatteryProbe: false, Event(0x0A, 1), Event(0x0A, 0));
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.BatteryReports.Any(r => r.Percent == 72),
                "the first session to report its probed percentage before failing mid-read");

            await Eventually.UntilAsync(
                () => context.BatteryReports.Any(r => r.Charge == ChargeState.Discharging),
                "the second session's stop edge to surface, exposing whatever percent the provider still remembers");

            var afterReconnect = context.BatteryReports.First(r => r.Charge == ChargeState.Discharging);
            Assert.Null(afterReconnect.Percent);
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_later_reply_in_the_same_session_replaces_the_level_the_probe_took()
    {
        // The dongle never volunteers a level, so the only way a second reading exists is the
        // provider asking again. A provider that asked only in the opening probe would then block on
        // reads that can only ever carry presence and charging, and with the dongle permanently
        // plugged in - the normal case - the displayed percentage would freeze at whatever it was
        // when the app started.
        //
        // The session is scripted to answer 72 and then 55, and it never ends: nothing here closes
        // the stream, throws, or runs out of transport script in a way that would open a second
        // session, so the single-session assertion below is what rules out "the level changed
        // because the provider reconnected and re-probed".
        var transport = new FakeHeadsetTransport().ThenSessionAnswering([72, 55]);
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.BatteryReports.Any(r => r.Percent == 72),
                "the opening probe's reading to be reported");

            await Eventually.UntilAsync(
                () => context.BatteryReports.Any(r => r.Percent == 55),
                "the provider to ask again inside the same session and report the newer reading");

            Assert.Single(transport.Sessions);
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_headset_switched_off_inside_a_live_session_stops_showing_a_level()
    {
        // Powering the headset off while the dongle stays plugged in is an event inside a healthy
        // session, so none of the three session-ending paths in Watch runs. Without the clearing this
        // test pins, the catalogue keeps the last percentage and the Battery page reads "not detected,
        // 72%" - and the moment the headset comes back that stale figure is eligible for the tray.
        //
        // The session answers exactly once, so nothing after the switch-off can re-establish a level:
        // any percentage still being reported at the end is a remembered one.
        var transport = new FakeHeadsetTransport().ThenSessionAnswering([72], Event(0x0C, 0));
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.BatteryReports.Any(r => r.Percent == 72),
                "the opening probe's reading to be reported before the headset is switched off");

            await Eventually.UntilAsync(
                () => context.PresenceOf(SourceId) == Presence.Absent,
                "the switch-off event to be reported as absent");

            await Eventually.UntilAsync(
                () => context.BatteryReports[^1] is { Percent: null, Charge: ChargeState.Unknown },
                "the switch-off to clear the remembered percentage rather than leave it on screen");

            Assert.Single(transport.Sessions);
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_charging_stop_edge_decays_to_unknown_as_the_host_sees_it()
    {
        // HyperXChargingTracker ages a "charging stopped" edge out after ChargingStale, because the
        // cable may have gone back in while nothing was listening. Its own unit tests prove Current()
        // does that; this proves the host is ever told, which needs something to call Current() on a
        // clock rather than only on an edge. That something is the battery poll.
        //
        // The 0x0A/state-0 report is consumed by the probe, which observes the stop edge and then
        // returns the presence the same report states - so ReadEvents starts with a stop edge already
        // in the tracker, no remembered percentage, and no further traffic of any kind. Every report
        // after that point therefore comes from the poll, and the only thing that can change what it
        // says is the passage of ChargingStale.
        var transport = new FakeHeadsetTransport().ThenSession(answersBatteryProbe: false, Event(0x0A, 0));
        var provider = new HyperXHidProvider(transport, Fast, TimeProvider.System);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.BatteryReports.Any(r => r.Charge == ChargeState.Discharging),
                "the stop edge to be reported as discharging while it is still believable");

            // Everything from here on is a poll's doing. Only reports that arrive after this point
            // are looked at, so the Unknown the provider declared at startup - before any of this -
            // cannot be mistaken for the decay.
            var beforeDecay = context.BatteryReports.Count;

            await Eventually.UntilAsync(
                () => context.BatteryReports.Skip(beforeDecay).Any(r => r.Charge == ChargeState.Unknown),
                "the stop edge to age out into Unknown once ChargingStale has passed, as the host sees it");
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static byte[] Event(byte tag, byte state)
    {
        var report = new byte[64];
        report[0] = 0x0D;
        report[1] = 0x02;
        report[2] = 0x03;
        report[3] = 0x00;
        report[4] = tag;
        report[5] = state;
        return report;
    }
}
