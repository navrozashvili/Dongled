using System.Diagnostics;
using Dongled.Abstractions;
using Dongled.Plugin.Logitech;
using Xunit;

namespace Dongled.Plugins.Tests.Logitech;

public sealed class LogitechGhubProviderTests
{
    private const string PowerplaySignature = "logitech:ghub:signature:CHARGE_PAD.powerplay.0.808994568";
    private const string MouseSignature = "logitech:ghub:signature:MOUSE.g502x_plus.0.3394205497";

    private static readonly LogitechGhubTimings Fast = new(TickInterval: TimeSpan.FromMilliseconds(20));

    /// <summary>A path that does not exist, so every test runs on the defaults.</summary>
    private static string NoConfig => Path.Combine(Path.GetTempPath(), $"asw-absent-{Guid.NewGuid():N}.json");

    [Fact]
    public void The_metadata_is_the_identity_the_plugin_ships_with()
    {
        var provider = new LogitechGhubProvider(new FakeGhubChannelFactory(), Fast, NoConfig);

        Assert.Equal("logitech.ghub", provider.Metadata.Id);
        Assert.Equal("Logitech G HUB", provider.Metadata.DisplayName);
        Assert.False(provider.Metadata.IsExperimental);
    }

    [Fact]
    public async Task Starting_publishes_the_aggregate_source_before_the_agent_has_said_anything()
    {
        // The picker should list "Any Logitech device" even with G HUB shut down, so this source is
        // hand-written rather than derived from a device.
        var channels = new FakeGhubChannelFactory().ThenRefused();
        var provider = new LogitechGhubProvider(channels, Fast, NoConfig);
        var context = new FakeProviderContext();

        var elapsed = Stopwatch.StartNew();
        await provider.StartAsync(context, TestContext.Current.CancellationToken);
        elapsed.Stop();

        try
        {
            var descriptor = Assert.Single(Assert.Single(context.Publications));
            Assert.Equal(GhubCatalogue.AnySourceId, descriptor.SourceId);
            Assert.Equal("Any Logitech device", descriptor.DisplayName);
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
    public async Task The_hello_is_consumed_and_the_bootstrap_requests_follow_it()
    {
        var channels = new FakeGhubChannelFactory()
            .ThenOpenConversation(GhubPayloads.Hello);
        var provider = new LogitechGhubProvider(channels, Fast, NoConfig);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => channels.Created.Count == 1 && channels.Created[0].Sent.Count >= 2,
                "the list request and the subscription to be sent");

            var sent = channels.Created[0].Sent;
            Assert.Contains("/devices/list", sent[0], StringComparison.Ordinal);
            Assert.Contains("SUBSCRIBE", sent[1], StringComparison.Ordinal);
            Assert.Contains("/devices/state/changed", sent[1], StringComparison.Ordinal);

            // The hello is not a device message, so it changes nothing: only the startup publication.
            Assert.Single(context.Publications);
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_device_list_publishes_every_source_and_reports_each_ones_presence()
    {
        var channels = new FakeGhubChannelFactory()
            .ThenOpenConversation(GhubPayloads.Hello, GhubPayloads.DevicesList);
        var provider = new LogitechGhubProvider(channels, Fast, NoConfig);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.LatestSources.Count == 7,
                "the aggregate plus three models and three signatures to be published");

            Assert.Equal(Presence.Present, context.PresenceOf(GhubCatalogue.AnySourceId));
            Assert.Equal(Presence.Present, context.PresenceOf(PowerplaySignature));

            // Published even though it is switched off, and reported absent rather than omitted.
            Assert.Contains(context.LatestSources, s => s.SourceId == MouseSignature);
            Assert.Equal(Presence.Absent, context.PresenceOf(MouseSignature));
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_state_change_merges_into_what_is_known_rather_than_replacing_it()
    {
        var channels = new FakeGhubChannelFactory()
            .ThenOpenConversation(
                GhubPayloads.Hello,
                GhubPayloads.DevicesList,
                GhubPayloads.StateChanged("dev00000001", "NOT_CONNECTED"));
        var provider = new LogitechGhubProvider(channels, Fast, NoConfig);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.PresenceOf(PowerplaySignature) == Presence.Absent,
                "the powerplay to go absent after its state change");

            // The delta carried only an id and a state. If it had replaced the device instead of merging
            // into it, the signature-keyed source would have disappeared along with its display name.
            var powerplay = Assert.Single(context.LatestSources, s => s.SourceId == PowerplaySignature);
            Assert.Equal("POWERPLAY Wireless Charging System", powerplay.DisplayName);
            Assert.Equal(7, context.LatestSources.Count);
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task An_unchanged_device_set_is_not_republished_but_presence_is_reported_again()
    {
        // The refresh re-reads the whole list every few seconds. Republishing an identical descriptor
        // set allocates for nothing; repeating a presence is free by construction.
        var channels = new FakeGhubChannelFactory()
            .ThenOpenConversation(GhubPayloads.Hello, GhubPayloads.DevicesList, GhubPayloads.DevicesList);
        var provider = new LogitechGhubProvider(channels, Fast, NoConfig);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.PresenceReports.Count(r => r.SourceId == GhubCatalogue.AnySourceId) >= 2,
                "the aggregate's presence to be reported for both list messages");

            // One publication at startup, one when the first list arrived, and none for the second.
            Assert.Equal(2, context.Publications.Count);
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Anything_the_agent_sends_that_is_not_a_device_message_is_ignored()
    {
        var channels = new FakeGhubChannelFactory()
            .ThenOpenConversation(
                GhubPayloads.Hello,
                "this is not json",
                """{ "path": "/some/other/thing", "payload": {} }""",
                GhubPayloads.DevicesList);
        var provider = new LogitechGhubProvider(channels, Fast, NoConfig);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.LatestSources.Count == 7,
                "the real list to be handled after the junk");

            // Startup, then the list. The junk in between published nothing.
            Assert.Equal(2, context.Publications.Count);
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task An_agent_that_closes_during_the_handshake_is_reconnected_to()
    {
        var channels = new FakeGhubChannelFactory()
            .ThenConversation((string?)null)
            .ThenOpenConversation(GhubPayloads.Hello, GhubPayloads.DevicesList);
        var provider = new LogitechGhubProvider(channels, Fast, NoConfig);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.LatestSources.Count == 7,
                "the provider to reconnect and read the list from the second connection");

            Assert.True(channels.Created[0].Disposed, "the failed connection should be released");
            Assert.True(context.Recorded.Mentions("handshake"));
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_refused_connection_is_explained_and_retried()
    {
        var channels = new FakeGhubChannelFactory()
            .ThenRefused("no agent is listening")
            .ThenOpenConversation(GhubPayloads.Hello, GhubPayloads.DevicesList);
        var provider = new LogitechGhubProvider(channels, Fast, NoConfig);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.LatestSources.Count == 7,
                "the provider to recover on its next attempt");

            Assert.True(context.Recorded.Mentions("no agent is listening"));
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task An_outage_reports_nothing_until_the_grace_period_has_elapsed()
    {
        // The default grace is eight seconds and this test does not wait for it. What is under test is
        // that the provider stays quiet in the meantime rather than reporting the moment it loses the
        // agent, which is what stops a websocket blip from interrupting the user's audio.
        var channels = new FakeGhubChannelFactory()
            .ThenConversation(GhubPayloads.Hello, GhubPayloads.DevicesList);
        var provider = new LogitechGhubProvider(channels, Fast, NoConfig);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.PresenceOf(PowerplaySignature) == Presence.Present,
                "the powerplay to be reported present before the agent goes away");

            // The scripted connection has now closed and the provider is in its outage window.
            await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

            Assert.Equal(Presence.Present, context.PresenceOf(PowerplaySignature));
            Assert.Equal(Presence.Present, context.PresenceOf(GhubCatalogue.AnySourceId));
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Once_the_grace_period_elapses_every_published_source_goes_absent_exactly_once()
    {
        using var config = new TemporaryConfig("""
            { "disconnectGraceSeconds": 0, "reconnectSeconds": 1 }
            """);
        var channels = new FakeGhubChannelFactory()
            .ThenConversation(GhubPayloads.Hello, GhubPayloads.DevicesList);
        var provider = new LogitechGhubProvider(channels, Fast, config.Path);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            await Eventually.UntilAsync(
                () => context.PresenceOf(GhubCatalogue.AnySourceId) == Presence.Absent
                    && context.PresenceOf(MouseSignature) == Presence.Absent,
                "every published source to be reported absent after the agent went away");

            // Never Unknown. The host schedules a disconnect return only when Absent arrives from
            // Present, so an Unknown in between would silently disable every rule on these sources.
            Assert.DoesNotContain(context.PresenceReports, r => r.Presence == Presence.Unknown);

            // The descriptor set is deliberately not republished during an outage: shrinking it back to
            // the aggregate would drop every device from the picker while G HUB restarts.
            Assert.Equal(7, context.LatestSources.Count);
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Stopping_releases_the_channel_and_reports_nothing_further()
    {
        var channels = new FakeGhubChannelFactory()
            .ThenOpenConversation(GhubPayloads.Hello, GhubPayloads.DevicesList);
        var provider = new LogitechGhubProvider(channels, Fast, NoConfig);
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);
        await Eventually.UntilAsync(
            () => context.LatestSources.Count == 7,
            "the list to be published before stopping");

        var beforeStop = context.PresenceReports.Count;
        await provider.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(beforeStop, context.PresenceReports.Count);
        Assert.All(channels.Created, c => Assert.True(c.Disposed));
    }

    [Fact]
    public async Task Stopping_a_provider_that_never_started_does_not_throw()
    {
        var provider = new LogitechGhubProvider(new FakeGhubChannelFactory(), Fast, NoConfig);

        await provider.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Cancelling_the_hosts_token_stops_the_loop()
    {
        var channels = new FakeGhubChannelFactory()
            .ThenOpenConversation(GhubPayloads.Hello, GhubPayloads.DevicesList);
        var provider = new LogitechGhubProvider(channels, Fast, NoConfig);
        var context = new FakeProviderContext();
        using var host = new CancellationTokenSource();

        await provider.StartAsync(context, host.Token);
        await Eventually.UntilAsync(
            () => context.LatestSources.Count == 7,
            "the list to be published");

        await host.CancelAsync();

        await Eventually.UntilAsync(
            () => channels.Created.All(c => c.Disposed),
            "the loop to notice the host's cancellation and release the channel");

        await provider.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Starts a provider against a channel the test keeps its own reference to, so it can call
    /// <see cref="FakeGhubChannel.Deliver"/> on it after the provider is already running - the battery
    /// tests need to drip-feed messages rather than script the whole conversation up front.
    /// </summary>
    private static async Task<ProviderHandle> StartProvider(FakeGhubChannel channel, FakeProviderContext context)
    {
        var channels = new FakeGhubChannelFactory().ThenChannel(channel);
        var provider = new LogitechGhubProvider(channels, Fast, NoConfig);
        await provider.StartAsync(context, TestContext.Current.CancellationToken);
        return new ProviderHandle(provider);
    }

    /// <summary>
    /// Advances the battery poll by two rounds, which is what it takes for a source that stopped
    /// answering to be declared away: the round whose battery GETs go unanswered, and the next round
    /// whose check notices they did. Detecting silence any sooner would mean declaring a source away
    /// before its own poll had a chance to be answered. Redelivering the device list stands in for a
    /// refresh tick, since the provider re-polls battery every time it applies a device list -
    /// including the periodic re-GET the real timer would have triggered. This only advances a round
    /// because <see cref="GhubPayloads.DevicesList"/> is a full list (<c>/devices/list</c>): the sweep
    /// that declares a source away runs on a full list only, precisely so that a burst of deltas
    /// (<see cref="GhubPayloads.StateChanged"/>, <c>/devices/state/changed</c>) cannot fire it.
    /// </summary>
    private static async Task AdvanceOneRefresh(FakeGhubChannel channel)
    {
        await channel.Deliver(GhubPayloads.DevicesList);
        await channel.Deliver(GhubPayloads.DevicesList);
    }

    [Fact]
    public async Task A_battery_reply_is_reported_against_the_devices_source()
    {
        var channel = FakeGhubChannel.DeliveringThenSilent([GhubPayloads.Hello]);
        var context = new FakeProviderContext();
        await using var provider = await StartProvider(channel, context);

        await channel.Deliver(GhubPayloads.DevicesList);
        await channel.Deliver(GhubPayloads.BatteryState);

        await Eventually.UntilAsync(
            () => context.BatteryReports.Any(r => r.Percent == 79),
            "the G502's battery reply to be reported");

        var report = context.BatteryReports.First(r => r.Percent == 79);
        Assert.Equal(MouseSignature, report.SourceId);
        Assert.Equal(ChargeState.Discharging, report.Charge);
    }

    [Fact]
    public async Task A_fully_charged_device_reads_as_full_not_discharging()
    {
        // Both flags are flipped true, not just fullyCharged: measured, the agent sets charging false
        // once a device is full, but if it ever sent both at once, checking charging first would still
        // report a docked, full mouse as running on its battery. Flipping only fullyCharged would not
        // pin the ordering FullyCharged-before-Charging is supposed to guarantee, because charging
        // already read false either way.
        var channel = FakeGhubChannel.DeliveringThenSilent([GhubPayloads.Hello]);
        var context = new FakeProviderContext();
        await using var provider = await StartProvider(channel, context);

        await channel.Deliver(GhubPayloads.DevicesList);
        await channel.Deliver(GhubPayloads.BatteryState
            .Replace("\"charging\": false", "\"charging\": true", StringComparison.Ordinal)
            .Replace("\"fullyCharged\": false", "\"fullyCharged\": true", StringComparison.Ordinal));

        await Eventually.UntilAsync(
            () => context.BatteryReports.Any(r => r.Charge == ChargeState.Full),
            "the fully-charged mouse to be reported Full rather than Charging, even though charging also reads true");
    }

    [Fact]
    public async Task A_no_such_path_answer_reports_nothing_and_logs_nothing_alarming()
    {
        // Most Logitech devices have no battery. This answer is the normal case, not a failure.
        var channel = FakeGhubChannel.DeliveringThenSilent([GhubPayloads.Hello]);
        var context = new FakeProviderContext();
        await using var provider = await StartProvider(channel, context);

        await channel.Deliver(GhubPayloads.DevicesList);
        await channel.Deliver(GhubPayloads.BatteryNoSuchPath);

        // A second round with nothing eventful to wait for from the no-such-path reply itself, so
        // this waits for proof that the pipeline moved past it cleanly: a second full battery poll,
        // which only happens once every message ahead of it - including the no-such-path reply - has
        // been handled.
        await channel.Deliver(GhubPayloads.DevicesList);
        await Eventually.UntilAsync(
            () => channel.Sent.Count(s => s.Contains("/battery/", StringComparison.Ordinal)) >= 6,
            "two full battery-poll rounds to have gone out, proving the no-such-path reply was handled without incident");

        // No SUCCESS is ever delivered in this scenario, so the collection being empty is necessarily
        // true of any implementation that compiles; it is stated here for completeness. The log
        // assertion below it is the one that actually discriminates this behaviour.
        Assert.Empty(context.BatteryReports);
        Assert.DoesNotContain(
            context.Recorded.Entries,
            e => e.Level >= ProviderLogLevel.Warning
                && e.Message.Contains("battery", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_source_a_battery_was_once_seen_for_stays_battery_capable_when_it_goes_away()
    {
        // The agent exposes no battery path at all for a disconnected device, so capability can only
        // be learned while it is present. Forgetting it would drop the device off the battery page
        // the moment it was switched off - the exact case the page exists to handle.
        var channel = FakeGhubChannel.DeliveringThenSilent([GhubPayloads.Hello]);
        var context = new FakeProviderContext();
        await using var provider = await StartProvider(channel, context);

        await channel.Deliver(GhubPayloads.DevicesList);
        await channel.Deliver(GhubPayloads.BatteryState);
        await Eventually.UntilAsync(
            () => context.BatteryReports.Any(r => r.Percent == 79),
            "the mouse's battery to be reported once");

        // FakeProviderContext.BatteryReports is a read-only snapshot with no Clear method, so the
        // count beforehand is recorded and what is asserted below looks only at what arrives after
        // this point, which is what this test actually cares about.
        var beforeAway = context.BatteryReports.Count;

        await channel.Deliver(GhubPayloads.BatteryNoSuchPath);
        await AdvanceOneRefresh(channel);

        await Eventually.UntilAsync(
            () => context.BatteryReports.Skip(beforeAway)
                .Any(r => r.Percent is null && r.SourceId == MouseSignature),
            "the mouse to be reported Unknown once its battery poll goes unanswered, while remaining battery-capable");
    }

    [Fact]
    public async Task A_burst_of_two_state_changed_deltas_does_not_wipe_a_good_battery_reading()
    {
        // G HUB emits /devices/state/changed deltas in a burst when a device connects or disconnects.
        // Sweeping for unanswered battery sources on every device-list message - including a delta -
        // means a second delta arriving before the first delta's re-polled battery reply comes back
        // wipes a perfectly good reading to (null, Unknown). The sweep must be keyed to a completed
        // poll round (the periodic full-list re-GET), not to message arrival, so a burst of deltas
        // between rounds must not touch the reading at all.
        var channel = FakeGhubChannel.DeliveringThenSilent([GhubPayloads.Hello]);
        var context = new FakeProviderContext();
        await using var provider = await StartProvider(channel, context);

        await channel.Deliver(GhubPayloads.DevicesList);
        await channel.Deliver(GhubPayloads.BatteryState);
        await Eventually.UntilAsync(
            () => context.BatteryReports.Any(r => r.Percent == 79),
            "the mouse's battery to be reported once");

        // See the comment on the equivalent line above: no Clear on this snapshot, so only what
        // arrives after this point is examined.
        var beforeBurst = context.BatteryReports.Count;

        // Two deltas back to back, with no battery reply delivered in between - the burst this test
        // is named for. Neither carries a battery reading of its own; the mouse's battery answer
        // never arrives again during this test, only the deltas' own re-polls do.
        await channel.Deliver(GhubPayloads.StateChanged("dev00000001", "ACTIVE"));
        await channel.Deliver(GhubPayloads.StateChanged("dev00000001", "ACTIVE"));

        // Wait for proof both deltas were handled - each re-polls all three known devices' batteries -
        // rather than asserting immediately on an unstarted pipeline.
        await Eventually.UntilAsync(
            () => channel.Sent.Count(s => s.Contains("/battery/", StringComparison.Ordinal)) >= 9,
            "both deltas' battery re-polls to have gone out");

        Assert.DoesNotContain(
            context.BatteryReports.Skip(beforeBurst),
            r => r.SourceId == MouseSignature && r.Percent is null);
    }

    /// <summary>Owns a started provider for the duration of an <c>await using</c>, and stops it after.</summary>
    private sealed class ProviderHandle(LogitechGhubProvider provider) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(provider.StopAsync(TestContext.Current.CancellationToken));
    }

    private sealed class TemporaryConfig : IDisposable
    {
        public TemporaryConfig(string content)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"asw-ghub-cfg-{Guid.NewGuid():N}.json");
            File.WriteAllText(Path, content);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }
    }
}
