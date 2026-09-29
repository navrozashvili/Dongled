using Dongled.Abstractions;
using Dongled.Plugin.HyperXHid;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Dongled.Plugins.Tests.HyperXHid;

public class HyperXChargingTrackerTests
{
    private static readonly TimeSpan Stale = TimeSpan.FromSeconds(2.5);

    private static (HyperXChargingTracker Tracker, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider();
        return (new HyperXChargingTracker(time, Stale), time);
    }

    [Fact]
    public void With_no_edge_ever_seen_the_state_is_unknown()
    {
        // The app started after the cable went in. There is genuinely no way to know, and saying
        // Unknown is the honest answer rather than a defect to work around.
        var (tracker, _) = Create();

        Assert.Equal(ChargeState.Unknown, tracker.Current());
    }

    [Fact]
    public void A_charging_edge_reads_as_charging()
    {
        var (tracker, _) = Create();

        tracker.Observe(charging: true);

        Assert.Equal(ChargeState.Charging, tracker.Current());
    }

    [Fact]
    public void Charging_is_latched_and_does_not_go_stale()
    {
        // The dongle announces charging once and never again. Ageing this out would flip a device
        // that is genuinely charging to Unknown a few seconds later.
        var (tracker, time) = Create();
        tracker.Observe(charging: true);

        time.Advance(TimeSpan.FromHours(3));

        Assert.Equal(ChargeState.Charging, tracker.Current());
    }

    [Fact]
    public void A_recent_stop_edge_reads_as_discharging()
    {
        var (tracker, time) = Create();
        tracker.Observe(charging: false);

        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(ChargeState.Discharging, tracker.Current());
    }

    [Fact]
    public void A_stale_stop_edge_decays_to_unknown()
    {
        // Past the window the app can no longer claim to know: the cable may have gone back in
        // while nothing was listening.
        var (tracker, time) = Create();
        tracker.Observe(charging: false);

        time.Advance(Stale + TimeSpan.FromMilliseconds(1));

        Assert.Equal(ChargeState.Unknown, tracker.Current());
    }

    [Fact]
    public void A_stop_edge_after_a_charging_edge_takes_effect()
    {
        var (tracker, _) = Create();
        tracker.Observe(charging: true);

        tracker.Observe(charging: false);

        Assert.Equal(ChargeState.Discharging, tracker.Current());
    }

    [Fact]
    public void Resetting_forgets_everything()
    {
        // Called when the session ends. What was true of the last headset says nothing about the
        // next one to pair with the dongle.
        var (tracker, _) = Create();
        tracker.Observe(charging: true);

        tracker.Reset();

        Assert.Equal(ChargeState.Unknown, tracker.Current());
    }
}
