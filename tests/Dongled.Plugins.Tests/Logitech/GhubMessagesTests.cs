using System.Text.Json;
using Dongled.Plugin.Logitech;
using Xunit;

namespace Dongled.Plugins.Tests.Logitech;

public sealed class GhubMessagesTests
{
    [Fact]
    public void A_request_uses_the_key_the_agent_was_measured_to_accept()
    {
        var json = GhubMessages.Request("GET", "/devices/list", 7);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // Measured against lghub_agent: a request sent with msg_id came back SUCCESS with the id echoed
        // as msgId. Either key works, so this pins a decision rather than a vendor requirement.
        Assert.Equal("asw:7", root.GetProperty("msg_id").GetString());
        Assert.Equal("GET", root.GetProperty("verb").GetString());
        Assert.Equal("/devices/list", root.GetProperty("path").GetString());
    }

    [Fact]
    public void The_real_devices_list_yields_every_device_with_its_display_names()
    {
        Assert.Equal(
            GhubUpdate.FullList,
            GhubMessages.TryReadDevices(GhubPayloads.DevicesList, out var devices));
        Assert.Equal(3, devices.Count);

        var mouse = devices.Single(d => d.Id == "dev00000000");
        Assert.Equal("NOT_CONNECTED", mouse.State);
        Assert.Equal("MOUSE", mouse.DeviceType);
        Assert.Equal("g502x_plus", mouse.DeviceModel);
        Assert.Equal("G502 X PLUS", mouse.DisplayName);
        Assert.Equal("G502 X PLUS Wireless Gaming Mouse", mouse.ExtendedDisplayName);
        Assert.Equal("MOUSE.g502x_plus.0.3394205497", mouse.Signature);
    }

    [Fact]
    public void A_state_change_for_one_device_is_a_delta_not_a_full_list()
    {
        // The distinction matters: a full list replaces what is known, a delta merges into it. Treating
        // a delta as a full list would drop every device the agent did not mention.
        Assert.Equal(
            GhubUpdate.Delta,
            GhubMessages.TryReadDevices(
                GhubPayloads.StateChanged("dev00000001", "NOT_CONNECTED"),
                out var devices));

        var device = Assert.Single(devices);
        Assert.Equal("dev00000001", device.Id);
        Assert.Equal("NOT_CONNECTED", device.State);

        // Absent fields come back empty rather than null, so a merge can tell "not mentioned" from
        // "mentioned as blank" by testing for emptiness in one place.
        Assert.Equal(string.Empty, device.DisplayName);
    }

    [Fact]
    public void A_state_change_carrying_a_list_is_also_a_delta()
    {
        const string Json = """
            {
             "path": "/devices/state/changed",
             "payload": { "deviceInfos": [ { "id": "dev00000000", "state": "ACTIVE" } ] }
            }
            """;

        Assert.Equal(GhubUpdate.Delta, GhubMessages.TryReadDevices(Json, out var devices));
        Assert.Equal("dev00000000", Assert.Single(devices).Id);
    }

    [Theory]
    // Not JSON at all.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this is not json")]
    [InlineData("{ unterminated")]
    [InlineData("[1, 2, 3]")]
    [InlineData("null")]
    [InlineData("\"a bare string\"")]
    // JSON, but not a message.
    [InlineData("{}")]
    [InlineData("""{ "verb": "GET" }""")]
    [InlineData("""{ "path": null }""")]
    [InlineData("""{ "path": "" }""")]
    [InlineData("""{ "path": 12 }""")]
    [InlineData("""{ "path": "/some/other/thing", "payload": {} }""")]
    // The right path, but the payload is wrong in every way it can be.
    [InlineData("""{ "path": "/devices/list" }""")]
    [InlineData("""{ "path": "/devices/list", "payload": null }""")]
    [InlineData("""{ "path": "/devices/list", "payload": "a string" }""")]
    [InlineData("""{ "path": "/devices/list", "payload": {} }""")]
    [InlineData("""{ "path": "/devices/list", "payload": { "deviceInfos": null } }""")]
    [InlineData("""{ "path": "/devices/list", "payload": { "deviceInfos": {} } }""")]
    [InlineData("""{ "path": "/devices/list", "payload": { "deviceInfos": "nope" } }""")]
    [InlineData("""{ "path": "/devices/state/changed", "payload": { "deviceInfo": 5 } }""")]
    public void Nothing_a_vendor_can_send_makes_parsing_throw(string json)
    {
        // Malformed documents find crashes that inspection misses. The contract is: parsing answers
        // None, it does not throw.
        var update = GhubMessages.TryReadDevices(json, out var devices);

        Assert.Equal(GhubUpdate.None, update);
        Assert.Empty(devices);
    }

    [Fact]
    public void A_device_list_containing_junk_entries_keeps_the_usable_ones()
    {
        const string Json = """
            {
             "path": "/devices/list",
             "payload": { "deviceInfos": [
               17,
               null,
               "a string",
               { "state": "ACTIVE" },
               { "id": "" },
               { "id": "dev00000009", "state": "ACTIVE", "displayName": "Real One" },
               { "id": "dev0000000a", "state": 12, "displayName": ["not", "a", "string"] }
             ] }
            }
            """;

        Assert.Equal(GhubUpdate.FullList, GhubMessages.TryReadDevices(Json, out var devices));

        // An entry with no usable id cannot be keyed, merged or reported on, so it is dropped. The last
        // entry has an id, so it survives with empty values for the fields that were the wrong type -
        // one bad field should not lose a whole device.
        Assert.Equal(["dev00000009", "dev0000000a"], devices.Select(d => d.Id));
        Assert.Equal("Real One", devices[0].DisplayName);
        Assert.Equal(string.Empty, devices[1].State);
        Assert.Equal(string.Empty, devices[1].DisplayName);
    }

    [Fact]
    public void An_empty_device_list_is_a_full_list_of_nothing_rather_than_no_answer()
    {
        // "G HUB knows about no devices" is real information: everything previously known is gone.
        // Answering None here would leave stale devices reported as present forever.
        Assert.Equal(
            GhubUpdate.FullList,
            GhubMessages.TryReadDevices(
                """{ "path": "/devices/list", "payload": { "deviceInfos": [] } }""",
                out var devices));

        Assert.Empty(devices);
    }

    [Fact]
    public void A_hello_is_recognised_as_nothing_to_act_on()
    {
        Assert.Equal(GhubUpdate.None, GhubMessages.TryReadDevices(GhubPayloads.Hello, out var devices));
        Assert.Empty(devices);
    }

    [Fact]
    public void The_battery_path_names_the_device()
    {
        Assert.Equal("/battery/dev00000000/state", GhubMessages.BatteryPath("dev00000000"));
    }

    [Fact]
    public void A_successful_battery_reply_is_read()
    {
        Assert.True(GhubMessages.TryReadBattery(GhubPayloads.BatteryState, out var battery));

        Assert.Equal("dev00000000", battery!.DeviceId);
        Assert.Equal(79, battery.Percent);
        Assert.False(battery.Charging);
        Assert.False(battery.FullyCharged);
    }

    [Fact]
    public void A_no_such_path_reply_is_not_a_battery()
    {
        // This is the capability test: the agent exposes no battery path for a device without one,
        // for a disconnected device, or for an id it does not know.
        Assert.False(GhubMessages.TryReadBattery(GhubPayloads.BatteryNoSuchPath, out var battery));
        Assert.Null(battery);
    }

    [Fact]
    public void A_device_list_message_is_not_a_battery()
    {
        Assert.False(GhubMessages.TryReadBattery(GhubPayloads.DevicesList, out _));
    }

    [Fact]
    public void A_success_reply_on_another_path_is_not_a_battery_even_with_a_root_deviceid()
    {
        // Claiming this as a battery reply would be sticky: HandleAsync maps it to a source and
        // remembers that source as battery-capable for the rest of the process, and there would be no
        // later reply to ever correct it. Unlike A_device_list_message_is_not_a_battery, this payload
        // has exactly the shape TryReadBattery reads - a SUCCESS code and a root-level string
        // deviceId - so only the path check can be what refuses it.
        const string Message = """
            {
             "path": "/devices/list",
             "result": { "code": "SUCCESS" },
             "payload": { "deviceId": "dev00000000", "percentage": 50 }
            }
            """;

        Assert.False(GhubMessages.TryReadBattery(Message, out var battery));
        Assert.Null(battery);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"path":"/battery/d/state","result":{"code":"SUCCESS"}}""")]
    // A string "percentage": without a ValueKind guard, TryGetInt32 throws InvalidOperationException
    // for any non-number ValueKind, which is not a JsonException, so it would escape the method's
    // catch and break the "never throws" contract.
    [InlineData("""{"result":{"code":"SUCCESS"},"payload":{"deviceId":"d","percentage":"79"}}""")]
    public void Unusable_input_is_refused_without_throwing(string message)
    {
        // The parsing half of this class never throws whatever it is handed; that is its contract.
        Assert.False(GhubMessages.TryReadBattery(message, out _));
    }

    [Fact]
    public void A_percentage_outside_zero_to_one_hundred_is_refused()
    {
        var message = GhubPayloads.BatteryState.Replace("\"percentage\": 79", "\"percentage\": 250", StringComparison.Ordinal);

        Assert.False(GhubMessages.TryReadBattery(message, out _));
    }

    [Fact]
    public void A_fully_charged_reply_says_so()
    {
        var message = GhubPayloads.BatteryState
            .Replace("\"fullyCharged\": false", "\"fullyCharged\": true", StringComparison.Ordinal);

        Assert.True(GhubMessages.TryReadBattery(message, out var battery));
        Assert.True(battery!.FullyCharged);
    }
}
