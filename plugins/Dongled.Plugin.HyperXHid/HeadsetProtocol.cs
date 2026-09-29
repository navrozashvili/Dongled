using Dongled.Abstractions;

namespace Dongled.Plugin.HyperXHid;

/// <summary>
/// The dongle's report format, as observed. Pure, so every branch is testable without a device.
/// </summary>
/// <remarks>
/// Observed from HyperX's own traffic. None of it is documented by the vendor, so nothing here is
/// derived - only recorded, and checked against real hardware.
/// </remarks>
internal static class HeadsetProtocol
{
    /// <summary>Report id the dongle sends unsolicited state changes on.</summary>
    public const byte EventReportId = 0x0D;

    /// <summary>Report id the battery request and its reply use.</summary>
    public const byte BatteryReportId = 0x0C;

    /// <summary>Command byte identifying a battery request, echoed in its reply.</summary>
    public const byte BatteryCommand = 0x06;

    /// <summary>Headset link and power state.</summary>
    public const byte TagLinkPower = 0x0C;

    /// <summary>Physical cable and power events.</summary>
    public const byte TagPhysical = 0x0A;

    /// <summary>
    /// The length the observed battery reply has. Shorter 0x0C traffic is something else. Observed,
    /// not derived, and confirmed against real hardware.
    /// </summary>
    private const int MinimumBatteryReplyLength = 9;

    private const int CommandIndex = 5;
    private const int BatteryPercentageIndex = 6;
    private const int TagIndex = 4;
    private const int StateIndex = 5;
    private const byte MaximumBatteryPercentage = 100;

    // CA1861 forbids inline array literals as arguments, so both patterns are hoisted.
    private static readonly byte[] EventPrefix = [0x0D, 0x02, 0x03, 0x00];

    private static readonly byte[] BatteryRequestHeader =
        [BatteryReportId, 0x02, 0x03, 0x01, 0x00, BatteryCommand];

    /// <summary>
    /// Fill <paramref name="report"/> with a battery request. The buffer is cleared first: a report is
    /// as long as the device's output report and everything after the header must be zero.
    /// </summary>
    public static void WriteBatteryRequest(Span<byte> report)
    {
        report.Clear();
        BatteryRequestHeader.CopyTo(report);
    }

    /// <summary>Whether this report is a battery request. The mirror of <see cref="WriteBatteryRequest"/>.</summary>
    /// <remarks>
    /// Used only by the test double, which needs to recognise the probe in order to answer it. Kept here
    /// rather than in the test so the header is written down once.
    /// </remarks>
    public static bool IsBatteryRequest(ReadOnlySpan<byte> report) =>
        report.Length >= BatteryRequestHeader.Length
        && report[..BatteryRequestHeader.Length].SequenceEqual(BatteryRequestHeader);

    /// <summary>
    /// Whether this report is the dongle's answer to a battery request. Used as a liveness probe: a
    /// headset that is off or unpaired does not answer, which is the only way to learn the current
    /// state without waiting for the next unsolicited event.
    /// </summary>
    public static bool IsBatteryReply(ReadOnlySpan<byte> report) =>
        report.Length >= MinimumBatteryReplyLength
        && report[0] == BatteryReportId
        && report[CommandIndex] == BatteryCommand
        && report[BatteryPercentageIndex] <= MaximumBatteryPercentage;

    /// <summary>
    /// The presence a connection event reports, or null if this report is not one. Null covers every
    /// other kind of traffic on the event report id, including battery levels, which share the id and
    /// would otherwise be read as a connection state.
    /// </summary>
    public static Presence? ReadPresenceEvent(ReadOnlySpan<byte> report)
    {
        if (report.Length <= StateIndex || report[0] != EventReportId)
        {
            return null;
        }

        if (!report[..EventPrefix.Length].SequenceEqual(EventPrefix))
        {
            return null;
        }

        var tag = report[TagIndex];
        if (tag != TagLinkPower && tag != TagPhysical)
        {
            return null;
        }

        return report[StateIndex] switch
        {
            0 => Presence.Absent,
            1 => Presence.Present,
            _ => null,
        };
    }

    /// <summary>
    /// The percentage a battery reply carries, or null if this report is not one.
    /// </summary>
    /// <remarks>
    /// Defined in terms of <see cref="IsBatteryReply"/> rather than repeating its checks, so the two
    /// cannot drift into disagreeing about what a battery reply is.
    /// </remarks>
    public static int? ReadBatteryPercentage(ReadOnlySpan<byte> report) =>
        IsBatteryReply(report) ? report[BatteryPercentageIndex] : null;

    /// <summary>
    /// Whether this report says charging started or stopped, or null if it is not a charging edge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads <see cref="TagPhysical"/>, the same reports <see cref="ReadPresenceEvent"/> reads. That
    /// is not a conflict: a cable going into the headset is simultaneously the headset being
    /// physically present and charging beginning, and both readings are true at once. The presence
    /// reading is unchanged by this method existing.
    /// </para>
    /// <para>
    /// The dongle speaks only on transitions — nothing in the periodic traffic carries a charging
    /// flag. A caller that started after the cable went in will therefore never see an edge and
    /// cannot know. That is a limitation of the device, and
    /// <see cref="HyperXChargingTracker"/> is where this plugin decides what to say about it.
    /// </para>
    /// </remarks>
    public static bool? ReadChargingEvent(ReadOnlySpan<byte> report)
    {
        if (report.Length <= StateIndex || report[0] != EventReportId)
        {
            return null;
        }

        if (!report[..EventPrefix.Length].SequenceEqual(EventPrefix))
        {
            return null;
        }

        if (report[TagIndex] != TagPhysical)
        {
            return null;
        }

        return report[StateIndex] switch
        {
            1 => true,
            0 => false,
            _ => null,
        };
    }
}
