using System.Linq;
using Dongled.Abstractions;
using Dongled.Core.Pipeline;
using Xunit;

namespace Dongled.Core.Tests.Pipeline;

public class SourceCatalogueTests
{
    [Fact]
    public void Publishing_replaces_a_providers_previous_set_rather_than_adding_to_it()
    {
        var catalogue = new SourceCatalogue();

        catalogue.ReplaceSources(
            "p1",
            [new AudioSourceDescriptor("a", "A", null), new AudioSourceDescriptor("b", "B", null)]);
        catalogue.ReplaceSources("p1", [new AudioSourceDescriptor("b", "B", null)]);

        // Each publish replaces the previous set, which is why the Rules picker can update live rather than growing
        // stale entries forever.
        Assert.Equal(["b"], catalogue.KnownSources().Select(source => source.SourceId));
    }

    [Fact]
    public void Publishing_an_empty_set_removes_everything_that_provider_had()
    {
        var catalogue = new SourceCatalogue();
        catalogue.ReplaceSources("p1", [new AudioSourceDescriptor("a", "A", null)]);

        catalogue.ReplaceSources("p1", []);

        Assert.Empty(catalogue.KnownSources());
    }

    [Fact]
    public void One_provider_replacing_its_set_leaves_another_providers_sources_alone()
    {
        var catalogue = new SourceCatalogue();
        catalogue.ReplaceSources("p1", [new AudioSourceDescriptor("a", "A", null)]);
        catalogue.ReplaceSources("p2", [new AudioSourceDescriptor("b", "B", null)]);

        catalogue.ReplaceSources("p1", []);

        Assert.Equal(["b"], catalogue.KnownSources().Select(source => source.SourceId));
    }

    [Fact]
    public void Known_sources_come_back_ordered_by_the_name_a_user_reads()
    {
        var catalogue = new SourceCatalogue();
        catalogue.ReplaceSources(
            "p1",
            [new AudioSourceDescriptor("z", "Alpha", null), new AudioSourceDescriptor("a", "Zulu", null)]);

        Assert.Equal(["Alpha", "Zulu"], catalogue.KnownSources().Select(source => source.DisplayName));
    }

    [Fact]
    public void Two_providers_claiming_one_source_identifier_yield_one_entry()
    {
        var catalogue = new SourceCatalogue();
        catalogue.ReplaceSources("p1", [new AudioSourceDescriptor("shared", "From one", null)]);
        catalogue.ReplaceSources("p2", [new AudioSourceDescriptor("SHARED", "From two", null)]);

        Assert.Single(catalogue.KnownSources());
    }

    [Fact]
    public void An_unreported_source_reads_as_unknown_rather_than_absent()
    {
        var catalogue = new SourceCatalogue();

        // Presence.Unknown is the zero value for exactly this reason: a source nobody has
        // reported on must not read as definitively not connected.
        Assert.Equal(Presence.Unknown, catalogue.GetPresence("never-heard-of"));
    }

    [Fact]
    public void Setting_presence_reports_what_it_was_before()
    {
        var catalogue = new SourceCatalogue();

        Assert.Equal(Presence.Unknown, catalogue.SetPresence("a", Presence.Present));
        Assert.Equal(Presence.Present, catalogue.SetPresence("a", Presence.Absent));
        Assert.Equal(Presence.Absent, catalogue.GetPresence("a"));
    }

    [Fact]
    public void Presence_matching_ignores_case_because_identifiers_arrive_from_several_sources()
    {
        var catalogue = new SourceCatalogue();
        catalogue.SetPresence("HyperX:Cloud", Presence.Present);

        Assert.Equal(Presence.Present, catalogue.GetPresence("hyperx:cloud"));
    }

    [Fact]
    public void Presence_survives_the_publishing_provider_dropping_the_source()
    {
        var catalogue = new SourceCatalogue();
        catalogue.ReplaceSources("p1", [new AudioSourceDescriptor("a", "A", null)]);
        catalogue.SetPresence("a", Presence.Present);

        catalogue.ReplaceSources("p1", []);

        // Presence is keyed by source, not by catalogue membership. A provider that briefly
        // republishes a smaller set must not make the engine forget that a rule's source is
        // connected and switch away from it.
        Assert.Equal(Presence.Present, catalogue.GetPresence("a"));
    }

    [Fact]
    public void A_blank_source_identifier_is_refused()
    {
        var catalogue = new SourceCatalogue();

        Assert.Throws<ArgumentException>(() => catalogue.SetPresence("  ", Presence.Present));
        Assert.Throws<ArgumentException>(() => catalogue.GetPresence(""));
    }

    [Fact]
    public void A_source_nobody_reported_a_battery_for_has_none()
    {
        Assert.Null(new SourceCatalogue().GetBattery("src:one"));
    }

    [Fact]
    public void A_battery_reading_is_kept_and_returned()
    {
        var catalogue = new SourceCatalogue();

        catalogue.SetBattery("src:one", new BatteryReading(72, ChargeState.Charging));

        var reading = catalogue.GetBattery("src:one");
        Assert.NotNull(reading);
        Assert.Equal(72, reading.Percent);
        Assert.Equal(ChargeState.Charging, reading.Charge);
    }

    [Fact]
    public void A_later_reading_replaces_an_earlier_one()
    {
        var catalogue = new SourceCatalogue();

        catalogue.SetBattery("src:one", new BatteryReading(72, ChargeState.Charging));
        catalogue.SetBattery("src:one", new BatteryReading(71, ChargeState.Discharging));

        Assert.Equal(71, catalogue.GetBattery("src:one")!.Percent);
    }

    [Fact]
    public void A_reading_with_no_percentage_still_marks_the_source_as_battery_capable()
    {
        var catalogue = new SourceCatalogue();

        catalogue.SetBattery("src:one", new BatteryReading(null, ChargeState.Unknown));

        Assert.NotNull(catalogue.GetBattery("src:one"));
        Assert.Null(catalogue.GetBattery("src:one")!.Percent);
    }

    [Fact]
    public void A_battery_reading_survives_the_provider_republishing_a_smaller_set()
    {
        // The rule presence already follows: dropping a source from a published set says nothing
        // about the source, so what was known about it is left alone.
        var catalogue = new SourceCatalogue();
        catalogue.ReplaceSources("p", [new AudioSourceDescriptor("src:one", "One", null)]);
        catalogue.SetBattery("src:one", new BatteryReading(50, ChargeState.Discharging));

        catalogue.ReplaceSources("p", []);

        Assert.Equal(50, catalogue.GetBattery("src:one")!.Percent);
    }

    [Fact]
    public void Source_identifiers_are_matched_without_regard_to_case()
    {
        var catalogue = new SourceCatalogue();

        catalogue.SetBattery("SRC:ONE", new BatteryReading(33, ChargeState.Discharging));

        Assert.Equal(33, catalogue.GetBattery("src:one")!.Percent);
    }

    [Fact]
    public void Known_source_states_carry_the_battery_reading()
    {
        var catalogue = new SourceCatalogue();
        catalogue.ReplaceSources("p", [new AudioSourceDescriptor("src:one", "One", null)]);
        catalogue.SetBattery("src:one", new BatteryReading(64, ChargeState.Discharging));

        Assert.Equal(64, Assert.Single(catalogue.KnownSourceStates()).Battery!.Percent);
    }

    [Fact]
    public void Known_source_states_carry_a_null_battery_for_a_source_with_none()
    {
        var catalogue = new SourceCatalogue();
        catalogue.ReplaceSources("p", [new AudioSourceDescriptor("src:one", "One", null)]);

        Assert.Null(Assert.Single(catalogue.KnownSourceStates()).Battery);
    }
}
