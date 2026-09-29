namespace Dongled.Plugin.HyperXHid;

/// <summary>
/// Finds and opens the dongle. The seam that keeps <see cref="HyperXHidProvider"/> testable without
/// hardware; <see cref="HidSharpHeadsetTransport"/> is the only implementation that touches a device.
/// </summary>
internal interface IHeadsetTransport
{
    /// <summary>
    /// Open the dongle's event interface, or return null if no usable dongle is present. An absent
    /// device is the ordinary case rather than a failure, so it is a null and not an exception.
    /// </summary>
    IHeadsetSession? TryOpen();
}

/// <summary>One open conversation with the dongle. Disposing it is idempotent.</summary>
internal interface IHeadsetSession : IDisposable
{
    /// <summary>Length of an input report on this interface.</summary>
    int InputReportLength { get; }

    /// <summary>Length of an output report on this interface.</summary>
    int OutputReportLength { get; }

    /// <summary>Send one output report.</summary>
    void Write(ReadOnlySpan<byte> report);

    /// <summary>
    /// Read one input report.
    /// </summary>
    /// <returns>
    /// False once the device has closed the stream, after which this session is finished. True with
    /// <paramref name="count"/> zero means the read timed out, which is the ordinary case: the dongle
    /// only speaks when something changes.
    /// </returns>
    bool TryRead(Span<byte> buffer, TimeSpan timeout, out int count);
}

/// <summary>
/// How long the provider waits for things. Injectable so a test of the retry loop does not wait real
/// seconds; production uses <see cref="Default"/>.
/// </summary>
/// <param name="EventReadTimeout">
/// How long one event read blocks. Also the worst case for <see cref="HyperXHidProvider.StopAsync"/>,
/// because a blocked read is what the watch loop is doing nearly all the time.
/// </param>
/// <param name="BatteryProbeTimeout">Total time to wait for a battery reply before deciding the headset is not answering.</param>
/// <param name="BatteryReadTimeout">How long one read inside the battery probe blocks.</param>
/// <param name="BatteryPoll">
/// How often, inside an open session, the plugin asks the headset for its level again.
/// <para>
/// The dongle never volunteers a battery level: it emits presence and charging events, and nothing
/// else. Without this tick the only reading a session ever has is the one its opening probe took, so
/// a permanently plugged-in dongle would freeze the displayed percentage for the life of the process.
/// </para>
/// <para>
/// The prior art (HyperXBatteryHID) polls every 2 seconds. This is deliberately slower: the headset
/// reports whole percent and drains over hours, so a faster poll buys no accuracy. It is not slower
/// still because this tick does two other jobs - it carries
/// <see cref="HyperXChargingTracker"/>'s staleness decay to the host, and it is what re-establishes a
/// level after a headset that was switched off comes back inside the same session. Both want tens of
/// seconds rather than minutes.
/// </para>
/// </param>
/// <param name="NoDongleRetryDelay">Wait before scanning again when no dongle is present.</param>
/// <param name="SessionRetryDelay">Wait before reopening after a session ended or failed.</param>
/// <param name="ChargingStale">
/// How long a "charging stopped" edge stays believable. Past this the plugin reports Unknown,
/// because the cable may have gone back in while nothing was listening. The dongle never repeats
/// itself, so this cannot be shortened into a poll.
/// </param>
internal sealed record HyperXHidTimings(
    TimeSpan EventReadTimeout,
    TimeSpan BatteryProbeTimeout,
    TimeSpan BatteryReadTimeout,
    TimeSpan BatteryPoll,
    TimeSpan NoDongleRetryDelay,
    TimeSpan SessionRetryDelay,
    TimeSpan ChargingStale)
{
    /// <summary>What the host gets.</summary>
    /// <remarks>
    /// The one-second event read timeout bounds StopAsync without needing a cancellation callback
    /// that disposes the stream mid-read, at the cost of waking once a second while nothing is
    /// happening.
    /// </remarks>
    public static HyperXHidTimings Default { get; } = new(
        EventReadTimeout: TimeSpan.FromSeconds(1),
        BatteryProbeTimeout: TimeSpan.FromMilliseconds(1500),
        BatteryReadTimeout: TimeSpan.FromMilliseconds(500),
        BatteryPoll: TimeSpan.FromSeconds(10),
        NoDongleRetryDelay: TimeSpan.FromSeconds(5),
        SessionRetryDelay: TimeSpan.FromSeconds(1),
        ChargingStale: TimeSpan.FromSeconds(2.5));
}
