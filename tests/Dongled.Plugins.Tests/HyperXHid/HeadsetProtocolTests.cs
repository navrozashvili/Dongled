using Dongled.Abstractions;
using Dongled.Plugin.HyperXHid;
using Xunit;

namespace Dongled.Plugins.Tests.HyperXHid;

public sealed class HeadsetProtocolTests
{
    [Fact]
    public void The_battery_request_is_the_vendor_header_in_a_zeroed_report()
    {
        var report = new byte[64];
        report[63] = 0xFF; // must be cleared: a stale buffer would send whatever was left in it

        HeadsetProtocol.WriteBatteryRequest(report);

        Assert.Equal([0x0C, 0x02, 0x03, 0x01, 0x00, 0x06], report[..6]);
        Assert.All(report[6..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void A_written_battery_request_is_recognised_as_one()
    {
        var report = new byte[64];
        HeadsetProtocol.WriteBatteryRequest(report);

        Assert.True(HeadsetProtocol.IsBatteryRequest(report));
        Assert.False(HeadsetProtocol.IsBatteryRequest(new byte[64]));
        Assert.False(HeadsetProtocol.IsBatteryRequest([]));
    }

    [Fact]
    public void A_battery_reply_needs_the_report_id_the_command_echo_and_a_plausible_percentage()
    {
        Assert.True(HeadsetProtocol.IsBatteryReply(Reply(percentage: 100)));
        Assert.True(HeadsetProtocol.IsBatteryReply(Reply(percentage: 0)));

        // 101% is not a battery level, so this is some other 0x0C traffic.
        Assert.False(HeadsetProtocol.IsBatteryReply(Reply(percentage: 101)));
    }

    [Theory]
    [InlineData(0)] // nothing at all
    [InlineData(6)] // one byte short of the percentage the reply is recognised by
    [InlineData(8)] // still shorter than the vendor's reply
    public void A_battery_reply_shorter_than_the_vendor_sends_is_refused(int length)
    {
        var reply = Reply(percentage: 50);

        Assert.False(HeadsetProtocol.IsBatteryReply(reply.AsSpan(0, length)));
    }

    [Fact]
    public void A_battery_reply_with_the_wrong_report_id_or_command_is_refused()
    {
        var wrongReportId = Reply(percentage: 50);
        wrongReportId[0] = 0x0D;
        Assert.False(HeadsetProtocol.IsBatteryReply(wrongReportId));

        var wrongCommand = Reply(percentage: 50);
        wrongCommand[5] = 0x07;
        Assert.False(HeadsetProtocol.IsBatteryReply(wrongCommand));
    }

    [Theory]
    [InlineData(0x0C, 1, Presence.Present)] // link/power on
    [InlineData(0x0C, 0, Presence.Absent)]  // link/power off
    [InlineData(0x0A, 1, Presence.Present)] // physical cable/power on
    [InlineData(0x0A, 0, Presence.Absent)]
    public void A_connection_event_becomes_a_presence(byte tag, byte state, Presence expected)
    {
        Assert.Equal(expected, HeadsetProtocol.ReadPresenceEvent(Event(tag, state)));
    }

    [Fact]
    public void A_report_that_is_not_a_connection_event_yields_no_presence()
    {
        // Right report id, wrong prefix: 0x0D traffic that is not an event report.
        var wrongPrefix = Event(0x0C, 1);
        wrongPrefix[2] = 0x04;
        Assert.Null(HeadsetProtocol.ReadPresenceEvent(wrongPrefix));

        // Wrong report id entirely.
        var wrongReportId = Event(0x0C, 1);
        wrongReportId[0] = 0x0C;
        Assert.Null(HeadsetProtocol.ReadPresenceEvent(wrongReportId));

        // A tag the plugin does not interpret. Battery level arrives on this path too, and reading it
        // as a connection event would report presence on every battery tick.
        Assert.Null(HeadsetProtocol.ReadPresenceEvent(Event(0x0B, 1)));

        // A state byte outside 0..1 is not a binary connection state.
        Assert.Null(HeadsetProtocol.ReadPresenceEvent(Event(0x0C, 2)));
        Assert.Null(HeadsetProtocol.ReadPresenceEvent(Event(0x0C, 0xFF)));

        // Truncated before the state byte.
        Assert.Null(HeadsetProtocol.ReadPresenceEvent(Event(0x0C, 1).AsSpan(0, 5)));
        Assert.Null(HeadsetProtocol.ReadPresenceEvent([]));
    }

    private static byte[] Reply(byte percentage)
    {
        var reply = new byte[64];
        reply[0] = 0x0C;
        reply[5] = 0x06;
        reply[6] = percentage;
        return reply;
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

    private static byte[] PhysicalEvent(byte state)
    {
        var report = new byte[9];
        report[0] = HeadsetProtocol.EventReportId;
        report[1] = 0x02;
        report[2] = 0x03;
        report[3] = 0x00;
        report[4] = HeadsetProtocol.TagPhysical;
        report[5] = state;
        return report;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(47)]
    [InlineData(100)]
    public void A_battery_reply_yields_its_percentage(byte percentage)
    {
        Assert.Equal(percentage, HeadsetProtocol.ReadBatteryPercentage(Reply(percentage)));
    }

    [Fact]
    public void A_report_that_is_not_a_battery_reply_yields_no_percentage()
    {
        // 101% is not a battery level, so this is other 0x0C traffic. The same judgement
        // IsBatteryReply makes, and the two must not disagree.
        Assert.Null(HeadsetProtocol.ReadBatteryPercentage(Reply(percentage: 101)));
    }

    [Fact]
    public void A_physical_event_yields_no_percentage()
    {
        Assert.Null(HeadsetProtocol.ReadBatteryPercentage(PhysicalEvent(1)));
    }

    [Fact]
    public void A_physical_event_is_also_a_charging_edge()
    {
        // The same report the presence reader consumes. A cable going in is both the headset being
        // physically there and charging starting; neither reading invalidates the other.
        Assert.True(HeadsetProtocol.ReadChargingEvent(PhysicalEvent(1)));
        Assert.False(HeadsetProtocol.ReadChargingEvent(PhysicalEvent(0)));
    }

    [Fact]
    public void The_presence_reading_of_a_physical_event_is_unchanged()
    {
        // Guard. This behaviour is daily-driven and must not drift while charging is added.
        Assert.Equal(Presence.Present, HeadsetProtocol.ReadPresenceEvent(PhysicalEvent(1)));
        Assert.Equal(Presence.Absent, HeadsetProtocol.ReadPresenceEvent(PhysicalEvent(0)));
    }

    [Fact]
    public void A_link_power_event_is_not_a_charging_edge()
    {
        // 0x0C is the headset powering on and off, which says nothing about a cable.
        var report = PhysicalEvent(1);
        report[4] = HeadsetProtocol.TagLinkPower;

        Assert.Null(HeadsetProtocol.ReadChargingEvent(report));
    }

    [Fact]
    public void A_battery_reply_is_not_a_charging_edge()
    {
        Assert.Null(HeadsetProtocol.ReadChargingEvent(Reply(50)));
    }

    [Fact]
    public void A_physical_event_with_an_unrecognised_state_is_not_a_charging_edge()
    {
        Assert.Null(HeadsetProtocol.ReadChargingEvent(PhysicalEvent(9)));
    }
}
