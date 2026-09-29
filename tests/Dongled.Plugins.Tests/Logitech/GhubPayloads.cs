namespace Dongled.Plugins.Tests.Logitech;

/// <summary>
/// Real G HUB traffic, captured from lghub_agent on 2026-07-27 and trimmed to the fields the plugin
/// reads. The real reply is about 40 KB for these three devices, nearly all of it lighting and input
/// capability trees. Key names, values and the mix of states are exactly as the agent sent them.
/// </summary>
internal static class GhubPayloads
{
    /// <summary>The unsolicited message the agent sends on connect, before anything is requested.</summary>
    public const string Hello = """
        { "msgId": "", "verb": "OPTIONS", "path": "/", "origin": "backend" }
        """;

    /// <summary>
    /// A GET /devices/list reply with three devices: a mouse that is switched off, a charging pad, and a
    /// second mouse. The ids are positional; the signatures are not.
    /// </summary>
    public const string DevicesList = """
        {
         "msgId": "asw:1",
         "verb": "GET",
         "path": "/devices/list",
         "origin": "backend",
         "result": { "code": "SUCCESS", "what": "" },
         "payload": {
          "@type": "type.googleapis.com/logi.protocol.devices.Device.Info.List",
          "deviceInfos": [
           {
            "id": "dev00000000",
            "state": "NOT_CONNECTED",
            "deviceType": "MOUSE",
            "deviceModel": "g502x_plus",
            "displayName": "G502 X PLUS",
            "extendedDisplayName": "G502 X PLUS Wireless Gaming Mouse",
            "deviceSignature": "MOUSE.g502x_plus.0.3394205497"
           },
           {
            "id": "dev00000001",
            "state": "ACTIVE",
            "deviceType": "CHARGE_PAD",
            "deviceModel": "powerplay",
            "displayName": "POWERPLAY",
            "extendedDisplayName": "POWERPLAY Wireless Charging System",
            "deviceSignature": "CHARGE_PAD.powerplay.0.808994568"
           },
           {
            "id": "dev00000002",
            "state": "ACTIVE",
            "deviceType": "MOUSE",
            "deviceModel": "pro_x_2_superstrike_wireless_mouse",
            "displayName": "PRO X2 SUPERSTRIKE",
            "extendedDisplayName": "PRO X2 SUPERSTRIKE Wireless Mouse",
            "deviceSignature": "MOUSE.pro_x_2_superstrike_wireless_mouse.0.614261177"
           }
          ]
         }
        }
        """;

    /// <summary>One device changing state, in the single-deviceInfo shape.</summary>
    public static string StateChanged(string id, string state) => $$"""
        {
         "msgId": "",
         "verb": "SUBSCRIBE",
         "path": "/devices/state/changed",
         "origin": "backend",
         "payload": { "deviceInfo": { "id": "{{id}}", "state": "{{state}}" } }
        }
        """;

    /// <summary>
    /// A successful battery reply, captured from lghub_agent on 2026-07-28 for a G502 X PLUS.
    /// Trimmed to the fields this plugin reads plus enough context to keep the shape honest.
    /// </summary>
    public const string BatteryState = """
        {
         "msgId": "asw:7",
         "verb": "GET",
         "path": "/battery/dev00000000/state",
         "origin": "backend",
         "result": { "code": "SUCCESS", "what": "" },
         "payload": {
          "@type": "type.googleapis.com/logi.protocol.wireless.Battery",
          "deviceId": "dev00000000",
          "percentage": 79,
          "charging": false,
          "criticalLevel": false,
          "chargingError": false,
          "fullyCharged": false,
          "isBatteryRemoved": false
         }
        }
        """;

    /// <summary>
    /// What the agent answers for a device with no battery, a disconnected device, or an unknown
    /// id. Captured 2026-07-28. This is the plugin's capability test.
    /// </summary>
    public const string BatteryNoSuchPath = """
        {
         "msgId": "asw:8",
         "verb": "GET",
         "path": "/battery/dev00000001/state",
         "origin": "backend",
         "result": {
          "code": "NO_SUCH_PATH",
          "what": "message path '/battery/dev00000001/state' not found."
         }
        }
        """;
}
