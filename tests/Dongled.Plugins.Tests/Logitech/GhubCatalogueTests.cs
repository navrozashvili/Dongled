using Dongled.Abstractions;
using Dongled.Plugin.Logitech;
using Xunit;

namespace Dongled.Plugins.Tests.Logitech;

public sealed class GhubCatalogueTests
{
    private static readonly string[] Active = ["ACTIVE"];

    [Fact]
    public void The_real_three_devices_become_seven_sources_with_real_display_names()
    {
        GhubMessages.TryReadDevices(GhubPayloads.DevicesList, out var devices);

        var sources = GhubCatalogue.Describe(devices);

        // One aggregate, plus a model-scoped and a signature-scoped source per device.
        Assert.Equal(7, sources.Count);

        // G HUB supplies real friendly names. Nothing here is a slug.
        var perDevice = sources.Single(
            s => s.SourceId == "logitech:ghub:signature:MOUSE.g502x_plus.0.3394205497");
        Assert.Equal("G502 X PLUS Wireless Gaming Mouse", perDevice.DisplayName);
        Assert.Equal("G HUB · MOUSE", perDevice.Detail);

        // deviceModel is already a slug in G HUB's own data ("g502x_plus"), so the model-scoped source
        // takes its name from displayName instead, the only field that is not a slug.
        var perModel = sources.Single(s => s.SourceId == "logitech:ghub:model:g502x_plus");
        Assert.Equal("G502 X PLUS", perModel.DisplayName);
        Assert.Equal("any G502 X PLUS in G HUB", perModel.Detail);
    }

    [Fact]
    public void The_aggregate_source_is_always_there_even_with_no_devices()
    {
        var only = Assert.Single(GhubCatalogue.Describe([]));

        Assert.Equal(GhubCatalogue.AnySourceId, only.SourceId);
        Assert.Equal("Any Logitech device", only.DisplayName);
    }

    [Fact]
    public void A_device_that_is_switched_off_is_still_published_as_a_source()
    {
        // Publishing only connected devices would make a device that is switched off vanish from the
        // picker and its rule become unselectable. Presence is what says whether it is here.
        GhubMessages.TryReadDevices(GhubPayloads.DevicesList, out var devices);

        Assert.Contains(
            GhubCatalogue.Describe(devices),
            s => s.SourceId == "logitech:ghub:signature:MOUSE.g502x_plus.0.3394205497");
        Assert.Equal(
            Presence.Absent,
            GhubCatalogue.Presences(devices, Active)["logitech:ghub:signature:MOUSE.g502x_plus.0.3394205497"]);
    }

    [Fact]
    public void The_per_device_source_keys_on_the_signature_and_never_on_the_positional_id()
    {
        // Measured: ids are positional (dev00000000, dev00000001, dev00000002 in enumeration order)
        // while a signature embeds the device's unit id. A rule persisted against a positional id would
        // silently point at a different device after G HUB re-enumerated.
        GhubMessages.TryReadDevices(GhubPayloads.DevicesList, out var devices);

        var sources = GhubCatalogue.Describe(devices);

        Assert.All(sources, s => Assert.DoesNotContain("dev0000", s.SourceId, StringComparison.Ordinal));
        Assert.Equal(
            3,
            sources.Count(s => s.SourceId.StartsWith(
                "logitech:ghub:signature:",
                StringComparison.Ordinal)));
    }

    [Fact]
    public void An_active_device_makes_its_own_source_its_model_and_the_aggregate_present()
    {
        GhubMessages.TryReadDevices(GhubPayloads.DevicesList, out var devices);

        var presences = GhubCatalogue.Presences(devices, Active);

        Assert.Equal(Presence.Present, presences[GhubCatalogue.AnySourceId]);
        Assert.Equal(Presence.Present, presences["logitech:ghub:model:powerplay"]);
        Assert.Equal(Presence.Present, presences["logitech:ghub:signature:CHARGE_PAD.powerplay.0.808994568"]);
        Assert.Equal(Presence.Absent, presences["logitech:ghub:model:g502x_plus"]);
    }

    [Fact]
    public void Nothing_is_ever_reported_as_unknown()
    {
        // The host records an Unknown but schedules a disconnect return only when Absent arrives from
        // Present, so an Unknown in the middle silently disables the rule.
        GhubMessages.TryReadDevices(GhubPayloads.DevicesList, out var devices);

        Assert.DoesNotContain(Presence.Unknown, GhubCatalogue.Presences(devices, Active).Values);
        Assert.DoesNotContain(Presence.Unknown, GhubCatalogue.Presences([], Active).Values);
    }

    [Fact]
    public void With_no_devices_the_aggregate_is_absent_rather_than_missing()
    {
        Assert.Equal(Presence.Absent, Assert.Single(GhubCatalogue.Presences([], Active)).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Two_devices_of_one_model_share_a_model_source_and_either_one_makes_it_present(
        bool connectedFirst)
    {
        // Both orderings, deliberately. With the connected device last, "overwrite the model's presence"
        // and "or it together" produce the same answer, so a single ordering cannot tell them apart.
        var off = Device("dev00000000", "NOT_CONNECTED", "g502x_plus", "MOUSE.g502x_plus.0.1");
        var on = Device("dev00000001", "ACTIVE", "g502x_plus", "MOUSE.g502x_plus.0.2");
        var devices = connectedFirst ? new[] { on, off } : [off, on];

        var sources = GhubCatalogue.Describe(devices);
        var presences = GhubCatalogue.Presences(devices, Active);

        // One model source, two signature sources, one aggregate.
        Assert.Equal(1, sources.Count(s => s.SourceId == "logitech:ghub:model:g502x_plus"));
        Assert.Equal(4, sources.Count);
        Assert.Equal(Presence.Present, presences["logitech:ghub:model:g502x_plus"]);
        Assert.Equal(Presence.Absent, presences["logitech:ghub:signature:MOUSE.g502x_plus.0.1"]);
        Assert.Equal(Presence.Present, presences["logitech:ghub:signature:MOUSE.g502x_plus.0.2"]);
    }

    [Fact]
    public void A_device_with_no_signature_or_model_contributes_only_to_the_aggregate()
    {
        // There is nothing stable to key a rule on, and inventing one from the positional id would
        // produce a rule that points somewhere else after a re-enumeration.
        var devices = new[] { Device("dev00000000", "ACTIVE", model: string.Empty, signature: string.Empty) };

        Assert.Equal(GhubCatalogue.AnySourceId, Assert.Single(GhubCatalogue.Describe(devices)).SourceId);
        Assert.Equal(
            Presence.Present,
            GhubCatalogue.Presences(devices, Active)[GhubCatalogue.AnySourceId]);
    }

    [Fact]
    public void The_configured_connected_states_decide_presence_and_are_matched_case_insensitively()
    {
        var devices = new[] { Device("dev00000000", "IdLe", "g502x_plus", "MOUSE.g502x_plus.0.1") };

        Assert.Equal(Presence.Absent, GhubCatalogue.Presences(devices, Active)[GhubCatalogue.AnySourceId]);
        Assert.Equal(Presence.Present, GhubCatalogue.Presences(devices, ["IDLE"])[GhubCatalogue.AnySourceId]);
    }

    [Fact]
    public void Sources_come_back_in_a_stable_order_so_an_unchanged_set_compares_equal()
    {
        // The provider republishes only when the set changes, and it compares the lists directly.
        // Enumeration order out of a dictionary is not something to rely on.
        GhubMessages.TryReadDevices(GhubPayloads.DevicesList, out var devices);

        var first = GhubCatalogue.Describe(devices);
        var reversed = GhubCatalogue.Describe(devices.Reverse().ToList());

        Assert.Equal(first, reversed);
    }

    [Fact]
    public void The_display_name_falls_back_through_every_field_before_giving_up()
    {
        var sources = GhubCatalogue.Describe(
        [
            new GhubDevice("dev0", "ACTIVE", "MOUSE", "model_a", "Display", "Extended", "sig-a"),
            new GhubDevice("dev1", "ACTIVE", "MOUSE", "model_b", "Display", string.Empty, "sig-b"),
            new GhubDevice("dev2", "ACTIVE", "MOUSE", "model_c", string.Empty, string.Empty, "sig-c"),
            new GhubDevice("dev3", "ACTIVE", "MOUSE", string.Empty, string.Empty, string.Empty, "sig-d"),
        ]);

        Assert.Equal("Extended", Name(sources, "sig-a"));
        Assert.Equal("Display", Name(sources, "sig-b"));
        Assert.Equal("model_c", Name(sources, "sig-c"));

        // Nothing else left: the id is the last resort, a display name of last resort rather than an
        // identifier being reused as one.
        Assert.Equal("dev3", Name(sources, "sig-d"));
    }

    private static string Name(IReadOnlyList<AudioSourceDescriptor> sources, string signature) =>
        sources.Single(s => s.SourceId == "logitech:ghub:signature:" + signature).DisplayName;

    private static GhubDevice Device(string id, string state, string model, string signature) =>
        new(
            id,
            state,
            "MOUSE",
            model,
            model.Length == 0 ? string.Empty : model.ToUpperInvariant(),
            string.Empty,
            signature);
}
