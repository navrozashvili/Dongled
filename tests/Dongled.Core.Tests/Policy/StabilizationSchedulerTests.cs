using System.Collections.Generic;
using System.Threading.Channels;
using Dongled.Core.Pipeline;
using Dongled.Core.Policy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Dongled.Core.Tests.Policy;

public class StabilizationSchedulerTests
{
    private static (StabilizationScheduler Scheduler, Channel<EngineSignal> Channel, FakeTimeProvider Time) Create()
    {
        var channel = Channel.CreateBounded<EngineSignal>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        var time = new FakeTimeProvider();
        return (new StabilizationScheduler(channel.Writer, time, NullLogger.Instance), channel, time);
    }

    private static List<StabilizationElapsed> Drain(Channel<EngineSignal> channel)
    {
        var elapsed = new List<StabilizationElapsed>();
        while (channel.Reader.TryRead(out var signal))
        {
            elapsed.Add(Assert.IsType<StabilizationElapsed>(signal));
        }

        return elapsed;
    }

    [Fact]
    public void Nothing_is_queued_before_the_delay_elapses()
    {
        var (scheduler, channel, time) = Create();
        using var owned = scheduler;

        scheduler.Schedule("a", TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(4));

        Assert.Empty(Drain(channel));
        Assert.True(scheduler.HasPending("a"));
    }

    [Fact]
    public void The_signal_is_queued_when_the_delay_elapses()
    {
        var (scheduler, channel, time) = Create();
        using var owned = scheduler;

        scheduler.Schedule("a", TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(5));

        var signal = Assert.Single(Drain(channel));
        Assert.Equal("a", signal.SourceId);
    }

    [Fact]
    public void Cancelling_inside_the_window_stops_the_signal_from_ever_arriving()
    {
        var (scheduler, channel, time) = Create();
        using var owned = scheduler;
        scheduler.Schedule("a", TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(3));

        Assert.True(scheduler.Cancel("a"));
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Empty(Drain(channel));
        Assert.False(scheduler.HasPending("a"));
    }

    [Fact]
    public void Cancelling_something_that_is_not_pending_reports_that_nothing_was_cancelled()
    {
        var (scheduler, _, _) = Create();
        using var owned = scheduler;

        Assert.False(scheduler.Cancel("a"));
    }

    [Fact]
    public void Rescheduling_restarts_the_delay_rather_than_keeping_the_first_one()
    {
        var (scheduler, channel, time) = Create();
        using var owned = scheduler;

        scheduler.Schedule("a", TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(4));
        scheduler.Schedule("a", TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(4));

        Assert.Empty(Drain(channel));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(Drain(channel));
    }

    [Fact]
    public void A_zero_delay_still_produces_exactly_one_matchable_signal()
    {
        var (scheduler, channel, time) = Create();
        using var owned = scheduler;

        // A zero delay is allowed, since the configuration store clamps to a floor of zero rather
        // than forbidding it. This checks that it produces a signal the consumer will accept
        // rather than one discarded as stale, which is what a user who set the delay to zero
        // would expect and what would otherwise fail silently.
        scheduler.Schedule("a", TimeSpan.Zero);
        time.Advance(TimeSpan.FromMilliseconds(1));

        var signal = Assert.Single(Drain(channel));
        Assert.True(scheduler.TryComplete(signal.SourceId, signal.Epoch));
    }

    [Fact]
    public void A_signal_from_a_superseded_scheduling_is_refused()
    {
        var (scheduler, channel, time) = Create();
        using var owned = scheduler;

        scheduler.Schedule("a", TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(5));
        var stale = Assert.Single(Drain(channel));

        // Disconnect, reconnect, disconnect again faster than the queue drains, and two of these
        // are in flight at once. Only the current one may be applied.
        scheduler.Schedule("a", TimeSpan.FromSeconds(5));

        Assert.False(scheduler.TryComplete(stale.SourceId, stale.Epoch));
        Assert.True(scheduler.HasPending("a"));
    }

    [Fact]
    public void Completing_a_scheduling_clears_it()
    {
        var (scheduler, channel, time) = Create();
        using var owned = scheduler;
        scheduler.Schedule("a", TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(5));
        var signal = Assert.Single(Drain(channel));

        Assert.True(scheduler.TryComplete(signal.SourceId, signal.Epoch));

        Assert.False(scheduler.HasPending("a"));
        Assert.False(scheduler.TryComplete(signal.SourceId, signal.Epoch));
    }

    [Fact]
    public void Source_identifiers_are_matched_ignoring_case()
    {
        var (scheduler, _, _) = Create();
        using var owned = scheduler;

        scheduler.Schedule("HyperX:Cloud", TimeSpan.FromSeconds(5));

        Assert.True(scheduler.HasPending("hyperx:cloud"));
        Assert.True(scheduler.Cancel("HYPERX:CLOUD"));
    }

    [Fact]
    public void Several_sources_wait_independently()
    {
        var (scheduler, channel, time) = Create();
        using var owned = scheduler;

        scheduler.Schedule("a", TimeSpan.FromSeconds(2));
        scheduler.Schedule("b", TimeSpan.FromSeconds(6));
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(["a"], Drain(channel).ConvertAll(signal => signal.SourceId));

        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(["b"], Drain(channel).ConvertAll(signal => signal.SourceId));
    }

    [Fact]
    public void Disposing_abandons_everything_pending()
    {
        var (scheduler, channel, time) = Create();
        scheduler.Schedule("a", TimeSpan.FromSeconds(5));

        scheduler.Dispose();
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Empty(Drain(channel));
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        var (scheduler, _, _) = Create();

        scheduler.Dispose();
        scheduler.Dispose();
    }
}
