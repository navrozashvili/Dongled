using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dongled.Abstractions;
using Dongled.Core.Audio;
using Dongled.Core.Configuration;
using Dongled.Core.Engine;
using Dongled.Core.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Dongled.Core.Tests.Engine;

/// <summary>
/// Switching policy and the signal pipeline end to end, plus the startup cases that need the whole
/// engine rather than the reconciler alone.
/// </summary>
public class SwitchingEngineTests : IAsyncDisposable
{
    private readonly FakeAudioEndpointService _endpoints = new();
    private readonly RecordingStateStore _state = new();
    private readonly StubConfigStore _config = new();
    private readonly FakeTimeProvider _time = new();
    private SwitchingEngine? _engine;
    private Task? _running;

    public SwitchingEngineTests()
    {
        _endpoints.AddActive("speakers", "Speakers");
        _endpoints.AddActive("headset", "Headset");
        _endpoints.AddActive("mic-headset", "Mic headset");
        _endpoints.AddActive("fallback", "Fallback");
        _endpoints.Defaults = new DefaultEndpoints("speakers", "speakers");
    }

    public async ValueTask DisposeAsync()
    {
        _engine?.CompleteInput();
        if (_running is not null)
        {
            await _running;
        }

        _engine?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_start_deadline_and_the_presence_resolution_ceiling_are_different_numbers()
    {
        // The two are routinely conflated, and the SDK documentation names only the first. Ten
        // seconds is how long a provider gets to establish watching; five is how long the host
        // waits for presences to resolve before treating what is left as absent.
        Assert.Equal(TimeSpan.FromSeconds(10), Core.Pipeline.ProviderRunner.StartDeadline);
        Assert.Equal(TimeSpan.FromSeconds(5), SwitchingEngine.PresenceResolutionCeiling);
        Assert.NotEqual(Core.Pipeline.ProviderRunner.StartDeadline, SwitchingEngine.PresenceResolutionCeiling);
    }

    [Fact]
    public async Task A_source_becoming_present_switches_to_its_rules_target()
    {
        var engine = Start(RuleFor("headset-source", "headset"));
        var context = Context(engine);

        context.ReportPresence("headset-source", Presence.Present);
        await Settle(engine);

        Assert.Equal("headset", Assert.Single(_endpoints.SetCalls).EndpointId);
    }

    [Fact]
    public async Task Repeating_a_presence_switches_exactly_once()
    {
        var engine = Start(RuleFor("headset-source", "headset"));
        var context = Context(engine);

        for (var i = 0; i < 5; i++)
        {
            context.ReportPresence("headset-source", Presence.Present);
        }

        await Settle(engine);

        // ReportPresence is idempotent by construction, so a provider that
        // polls need not track what it last reported.
        Assert.Single(_endpoints.SetCalls);
    }

    [Fact]
    public async Task A_source_no_rule_names_is_ignored()
    {
        var engine = Start(RuleFor("headset-source", "headset"));
        var context = Context(engine);

        context.ReportPresence("something-else", Presence.Present);
        await Settle(engine);

        Assert.Empty(_endpoints.SetCalls);
    }

    [Fact]
    public async Task A_disconnect_waits_out_the_stabilization_delay_before_returning()
    {
        var engine = Start(RuleFor("headset-source", "headset"));
        var context = Context(engine);
        context.ReportPresence("headset-source", Presence.Present);
        await Settle(engine);
        _endpoints.SetCalls.Clear();

        context.ReportPresence("headset-source", Presence.Absent);
        await Settle(engine);
        Assert.Empty(_endpoints.SetCalls);

        _time.Advance(TimeSpan.FromSeconds(5));
        await Settle(engine);

        Assert.NotEmpty(_endpoints.SetCalls);
        Assert.All(_endpoints.SetCalls, call => Assert.Equal("speakers", call.EndpointId));
    }

    [Fact]
    public async Task Reconnecting_inside_the_stabilization_window_cancels_the_pending_return()
    {
        var engine = Start(RuleFor("headset-source", "headset"));
        var context = Context(engine);
        context.ReportPresence("headset-source", Presence.Present);
        await Settle(engine);

        context.ReportPresence("headset-source", Presence.Absent);
        await Settle(engine);
        _time.Advance(TimeSpan.FromSeconds(3));

        context.ReportPresence("headset-source", Presence.Present);
        await Settle(engine);
        _endpoints.SetCalls.Clear();

        _time.Advance(TimeSpan.FromSeconds(10));
        await Settle(engine);

        // The device flickered. Nothing should have come back to the speakers.
        Assert.Empty(_endpoints.SetCalls);
    }

    [Fact]
    public async Task A_source_that_was_never_connected_reporting_absent_schedules_no_return()
    {
        var engine = Start(RuleFor("headset-source", "headset"));
        var context = Context(engine);

        context.ReportPresence("headset-source", Presence.Absent);
        await Settle(engine);
        _time.Advance(TimeSpan.FromSeconds(30));
        await Settle(engine);

        // Nothing was ever switched, so there is nothing to come back from. The startup pass is
        // what decides whether the user is stuck on a device that is switched off.
        Assert.DoesNotContain(_endpoints.SetCalls, call => call.EndpointId == "speakers");
    }

    [Fact]
    public async Task Startup_resolution_completes_as_soon_as_every_awaited_source_reports()
    {
        var engine = Start(RuleFor("a", "headset"), RuleFor("b", "mic-headset"));
        var context = Context(engine);

        context.ReportPresence("a", Presence.Present);
        context.ReportPresence("b", Presence.Absent);

        // No clock advance: once every source has answered, startup finishes without waiting out
        // the resolution ceiling.
        await engine.StartupReconciliationCompleted.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Startup_resolution_gives_up_at_the_ceiling()
    {
        _endpoints.Defaults = new DefaultEndpoints("headset", "headset");
        _state.Entries["never-reports"] = new PreviousDefault("speakers", "speakers", DateTimeOffset.UnixEpoch);
        var engine = Start(RuleFor("never-reports", "headset"));

        Assert.False(engine.StartupReconciliationCompleted.IsCompleted);

        _time.Advance(SwitchingEngine.PresenceResolutionCeiling);
        await engine.StartupReconciliationCompleted.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        // Unknown at the ceiling is treated as absent, and the user is sitting on the target, so
        // the disconnect behaviour applies.
        Assert.NotEmpty(_endpoints.SetCalls);
        Assert.All(_endpoints.SetCalls, call => Assert.Equal("speakers", call.EndpointId));
    }

    [Fact]
    public async Task Startup_reconciliation_is_skipped_when_the_setting_is_off()
    {
        _endpoints.Defaults = new DefaultEndpoints("headset", "headset");
        _state.Entries["never-reports"] = new PreviousDefault("speakers", "speakers", DateTimeOffset.UnixEpoch);
        _config.Config = new AppConfig
        {
            App = new AppSettings { ApplyRulesOnStartup = false, DisconnectStabilizationSeconds = 5 },
            Rules = [RuleFor("never-reports", "headset")],
        };

        _engine = new SwitchingEngine(_endpoints, _config, _state, _time, NullLoggerFactory.Instance);
        _running = _engine.RunAsync(TestContext.Current.CancellationToken);

        await _engine.StartupReconciliationCompleted.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        Assert.Empty(_endpoints.SetCalls);
    }

    [Fact]
    public async Task Published_sources_become_the_known_catalogue()
    {
        var engine = Start();
        var context = Context(engine);

        context.PublishSources([new AudioSourceDescriptor("a", "Alpha", "detail")]);
        await Settle(engine);

        var source = Assert.Single(engine.KnownSources());
        Assert.Equal("Alpha", source.DisplayName);
    }

    [Fact]
    public async Task Reloading_configuration_makes_a_newly_added_rule_take_effect()
    {
        var engine = Start();
        var context = Context(engine);

        _config.Config = new AppConfig
        {
            App = new AppSettings { ApplyRulesOnStartup = true, DisconnectStabilizationSeconds = 5 },
            Rules = [RuleFor("headset-source", "headset")],
        };
        await Settle(engine);

        context.ReportPresence("headset-source", Presence.Present);
        await Settle(engine);

        Assert.Equal("headset", Assert.Single(_endpoints.SetCalls).EndpointId);
    }

    [Fact]
    public async Task A_reload_requested_while_the_queue_is_full_is_still_applied()
    {
        _config.Config = new AppConfig { App = new AppSettings { ApplyRulesOnStartup = false } };
        _engine = new SwitchingEngine(_endpoints, _config, _state, _time, NullLoggerFactory.Instance);
        var context = Context(_engine);

        // Nothing is draining yet, so exactly the queue's capacity fills it without blocking.
        for (var i = 0; i < 256; i++)
        {
            context.PublishSources([new AudioSourceDescriptor($"s{i}", $"S{i}", null)]);
        }

        // Must return rather than wait for room: the caller is the UI thread.
        var request = Task.Run(_engine.RequestConfigurationReload, TestContext.Current.CancellationToken);
        await request.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        _running = _engine.RunAsync(TestContext.Current.CancellationToken);

        // One read when the consumer starts, and one for the request that arrived while full.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (_config.LoadCount < 2)
        {
            await Task.Delay(2, timeout.Token);
        }

        Assert.Equal(2, _config.LoadCount);
    }

    [Fact]
    public async Task A_reload_requested_after_shutdown_is_ignored()
    {
        var engine = Start();
        engine.CompleteInput();
        await _running!.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var loads = _config.LoadCount;

        engine.RequestConfigurationReload();

        Assert.Equal(loads, _config.LoadCount);
    }

    [Fact]
    public async Task A_provider_flooding_the_pipeline_does_not_deadlock_the_consumer()
    {
        var engine = Start(RuleFor("headset-source", "headset"));
        var context = Context(engine);

        // Far more reports than the queue can hold, from a thread of its
        // own, while the consumer drains: the writer waits for room and nothing is lost.
        var flooding = Task.Run(
            () =>
            {
                for (var i = 0; i < 5000; i++)
                {
                    context.PublishSources([new AudioSourceDescriptor($"s{i}", $"S{i}", null)]);
                }
            },
            TestContext.Current.CancellationToken);

        await flooding.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        await Settle(engine);

        Assert.Single(engine.KnownSources());
    }

    [Fact]
    public async Task Per_provider_order_survives_several_providers_reporting_at_once()
    {
        var engine = Start();
        var contexts = Enumerable.Range(0, 4).Select(i => Context(engine, $"p{i}")).ToArray();

        await Task.WhenAll(contexts.Select((context, index) => Task.Run(
            () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    context.PublishSources([new AudioSourceDescriptor($"p{index}:s{i}", $"P{index} S{i}", null)]);
                }
            },
            TestContext.Current.CancellationToken)));

        await Settle(engine);

        // Each provider's set replaces its own, so the last one each published is what survives.
        Assert.Equal(
            ["p0:s199", "p1:s199", "p2:s199", "p3:s199"],
            engine.KnownSources().Select(source => source.SourceId).Order().ToArray());
    }

    [Fact]
    public async Task A_hostile_provider_cannot_stop_the_consumer()
    {
        var engine = Start(RuleFor("headset-source", "headset"));
        var context = Context(engine);

        // A consumer that dies takes every rule with it, so the corpus is worth running: nothing
        // here may end the loop.
        context.PublishSources([]);
        context.PublishSources([new AudioSourceDescriptor(new string('x', 100_000), "Huge", null)]);
        context.PublishSources([new AudioSourceDescriptor("a", new string('y', 100_000), null)]);
        context.ReportPresence("headset-source", (Presence)(-1));
        context.ReportPresence(new string('z', 100_000), Presence.Present);
        _endpoints.ThrowOnGetDefaults = true;
        context.ReportPresence("headset-source", Presence.Present);
        await Settle(engine);
        _endpoints.ThrowOnGetDefaults = false;

        context.ReportPresence("headset-source", Presence.Absent);
        context.ReportPresence("headset-source", Presence.Present);
        await Settle(engine);

        Assert.NotEmpty(_endpoints.SetCalls);
        Assert.Equal("headset", _endpoints.SetCalls[^1].EndpointId);
    }

    [Fact]
    public async Task Completing_the_input_ends_the_consumer()
    {
        var engine = Start();

        engine.CompleteInput();
        await _running!.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        _running = null;
    }

    [Fact]
    public async Task Cancelling_the_token_ends_the_consumer()
    {
        using var cts = new CancellationTokenSource();
        _config.Config = new AppConfig { App = new AppSettings { ApplyRulesOnStartup = false } };
        _engine = new SwitchingEngine(_endpoints, _config, _state, _time, NullLoggerFactory.Instance);
        var running = _engine.RunAsync(cts.Token);

        await cts.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_battery_report_shows_up_in_the_source_states()
    {
        var engine = Start();
        var context = Context(engine);

        context.PublishSources([new AudioSourceDescriptor("src:one", "One", null)]);
        context.ReportBattery("src:one", 41, ChargeState.Discharging);
        await Settle(engine);

        var state = Assert.Single(engine.SourceStates());
        Assert.Equal(41, state.Battery!.Percent);
        Assert.Equal(ChargeState.Discharging, state.Battery.Charge);
    }

    [Fact]
    public async Task A_battery_report_never_causes_a_switch()
    {
        // Battery is display state. A rule firing on a charge tick would be a defect, and this is
        // the guard that keeps someone from adding rule evaluation to that switch arm later.
        var engine = Start(RuleFor("src:one", "dev:one"));
        var context = Context(engine);

        context.PublishSources([new AudioSourceDescriptor("src:one", "One", null)]);
        context.ReportBattery("src:one", 41, ChargeState.Discharging);
        await Settle(engine);

        Assert.Empty(_endpoints.SetCalls);
    }

    private static Rule RuleFor(
        string sourceId,
        string target,
        DisconnectMode mode = DisconnectMode.RestorePrevious) => new()
        {
            Id = sourceId,
            Name = sourceId,
            Enabled = true,
            Source = new RuleSource { Id = sourceId },
            Target = new RuleTarget { DeviceId = target, Roles = [AudioRole.Media, AudioRole.Calls] },
            OnDisconnect = new DisconnectBehavior { Mode = mode, FallbackDeviceId = "fallback" },
        };

    private static IProviderContext Context(SwitchingEngine engine, string providerId = "p1") =>
        engine.CreateProviderContext(providerId, static () => LogLevel.Warning);

    private SwitchingEngine Start(params Rule[] rules)
    {
        _config.Config = new AppConfig
        {
            App = new AppSettings { ApplyRulesOnStartup = true, DisconnectStabilizationSeconds = 5 },
            Rules = [.. rules],
        };

        _engine = new SwitchingEngine(_endpoints, _config, _state, _time, NullLoggerFactory.Instance);
        _running = _engine.RunAsync(TestContext.Current.CancellationToken);
        return _engine;
    }

    /// <summary>
    /// Waits until the consumer has drained everything queued so far, by pushing a reload through
    /// and waiting for the read count to move. Polling a clock would make these tests flaky.
    /// </summary>
    private async Task Settle(SwitchingEngine engine)
    {
        var before = _config.LoadCount;
        engine.RequestConfigurationReload();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        while (_config.LoadCount == before)
        {
            await Task.Delay(2, timeout.Token);
        }
    }
}
