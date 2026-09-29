using Dongled.Abstractions;
using Dongled.App.Presentation;
using Dongled.Core.Pipeline;
using Xunit;

namespace Dongled.App.Tests.Presentation;

public class BatteryOrderingTests
{
    private static SourceState State(
        string id,
        Presence presence = Presence.Present,
        int? percent = 50,
        ChargeState charge = ChargeState.Discharging,
        bool hasBattery = true,
        string? name = null) =>
        new(
            new AudioSourceDescriptor(id, name ?? id, null),
            presence,
            hasBattery ? new BatteryReading(percent, charge) : null);

    [Fact]
    public void Sources_without_a_battery_are_left_out()
    {
        var ordered = BatteryOrdering.Order([State("a"), State("b", hasBattery: false)], []);

        Assert.Equal(["a"], ordered.Select(s => s.Descriptor.SourceId));
    }

    [Fact]
    public void A_switched_off_battery_device_is_still_listed()
    {
        // The requirement this whole feature turns on: the user must be able to order a device that
        // is currently off, so it is in line for when it comes back.
        var ordered = BatteryOrdering.Order([State("a", presence: Presence.Absent, percent: null)], []);

        Assert.Single(ordered);
    }

    [Fact]
    public void The_configured_order_wins()
    {
        var ordered = BatteryOrdering.Order([State("a"), State("b"), State("c")], ["c", "a"]);

        Assert.Equal(["c", "a", "b"], ordered.Select(s => s.Descriptor.SourceId));
    }

    [Fact]
    public void Unlisted_sources_follow_in_display_name_order()
    {
        var ordered = BatteryOrdering.Order([State("z", name: "Alpha"), State("y", name: "Beta")], []);

        Assert.Equal(["z", "y"], ordered.Select(s => s.Descriptor.SourceId));
    }

    [Fact]
    public void An_identifier_in_the_order_that_no_longer_exists_is_skipped()
    {
        var ordered = BatteryOrdering.Order([State("a")], ["gone", "a"]);

        Assert.Equal(["a"], ordered.Select(s => s.Descriptor.SourceId));
    }

    [Fact]
    public void The_configured_order_matches_identifiers_without_regard_to_case()
    {
        var ordered = BatteryOrdering.Order([State("a"), State("b")], ["B"]);

        Assert.Equal(["b", "a"], ordered.Select(s => s.Descriptor.SourceId));
    }

    [Fact]
    public void A_duplicated_identifier_in_the_saved_order_uses_its_first_position()
    {
        // A hand-edited file must not be able to push a source to the back by naming it twice.
        var ordered = BatteryOrdering.Order([State("a"), State("b")], ["a", "b", "a"]);

        Assert.Equal(["a", "b"], ordered.Select(s => s.Descriptor.SourceId));
    }

    [Fact]
    public void Saving_a_reorder_keeps_the_rank_of_a_source_that_is_not_on_screen()
    {
        // Only sources reporting a battery right now are on screen, so a disabled plugin - or a
        // device switched off since startup, which has therefore never declared a battery at all -
        // is absent from the list the user is dragging rows around in. AppSettings.BatteryDisplayOrder
        // promises those identifiers are kept rather than pruned: forgetting the user's ordering
        // because a dongle was unplugged would be a defect. Writing the visible rows over the saved
        // order wholesale breaks exactly that promise.
        var merged = BatteryOrdering.Merge(["b", "a"], ["a", "gone", "b", "also-gone"]);

        Assert.Equal(["b", "a", "gone", "also-gone"], merged);
    }

    [Fact]
    public void Saving_a_reorder_does_not_duplicate_a_source_whose_saved_case_differs()
    {
        // Identifiers are compared ordinal-ignore-case everywhere else in this codebase, so a saved
        // order whose casing drifted must not leave the same source listed twice - which would let
        // the stale, lower-cased copy outrank a source the user had just moved above it.
        var merged = BatteryOrdering.Merge(["a", "b"], ["B", "A"]);

        Assert.Equal(["a", "b"], merged);
    }

    [Fact]
    public void The_tray_takes_the_first_source_that_is_present_and_reporting()
    {
        // A source may be absent but still carry a stale percentage from before it disconnected.
        // The tray must prefer a present, reporting source over an absent one with an old reading.
        var ordered = BatteryOrdering.Order(
            [State("a", presence: Presence.Absent, percent: 40), State("b", percent: 30)],
            ["a", "b"]);

        Assert.Equal("b", BatteryOrdering.PickForTray(ordered)!.Descriptor.SourceId);
    }

    [Fact]
    public void A_present_source_with_no_reading_is_skipped_by_the_tray()
    {
        var ordered = BatteryOrdering.Order(
            [State("a", percent: null), State("b", percent: 30)],
            ["a", "b"]);

        Assert.Equal("b", BatteryOrdering.PickForTray(ordered)!.Descriptor.SourceId);
    }

    [Fact]
    public void A_zero_percent_reading_is_still_a_reading()
    {
        var ordered = BatteryOrdering.Order([State("a", percent: 0)], []);

        Assert.NotNull(BatteryOrdering.PickForTray(ordered));
    }

    [Fact]
    public void The_tray_picks_nothing_when_no_source_is_reporting()
    {
        Assert.Null(BatteryOrdering.PickForTray(BatteryOrdering.Order([State("a", percent: null)], [])));
    }

    [Theory]
    [InlineData(87, ChargeState.Discharging, "87%")]
    [InlineData(87, ChargeState.Charging, "87% charging")]
    [InlineData(100, ChargeState.Full, "full")]
    [InlineData(87, ChargeState.Unknown, "87%")]
    [InlineData(0, ChargeState.Discharging, "0%")]
    public void A_reading_formats_for_display(int percent, ChargeState charge, string expected)
    {
        Assert.Equal(expected, BatteryOrdering.Format(new BatteryReading(percent, charge)));
    }

    [Fact]
    public void A_reading_with_no_level_formats_as_a_dash()
    {
        Assert.Equal("—", BatteryOrdering.Format(new BatteryReading(null, ChargeState.Unknown)));
    }

    [Fact]
    public void A_charging_state_with_no_level_says_so()
    {
        Assert.Equal("charging", BatteryOrdering.Format(new BatteryReading(null, ChargeState.Charging)));
    }

    [Fact]
    public void No_reading_at_all_formats_as_a_dash()
    {
        Assert.Equal("—", BatteryOrdering.Format(null));
    }

    [Theory]
    [InlineData(Presence.Present, "connected")]
    [InlineData(Presence.Absent, "not detected")]
    [InlineData(Presence.Unknown, "not detected yet")]
    public void Presence_describes_itself_the_same_way_on_every_page(Presence presence, string expected)
    {
        Assert.Equal(expected, BatteryOrdering.StateFor(presence));
    }
}
