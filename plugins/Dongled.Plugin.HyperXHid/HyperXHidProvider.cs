using System.Diagnostics.CodeAnalysis;
using Dongled.Abstractions;

// One attribute per plugin assembly. This is the whole of discovery: the host never scans types and
// never runs a constructor while working out what this assembly is.
[assembly: AudioSourceProvider(typeof(Dongled.Plugin.HyperXHid.HyperXHidProvider))]

namespace Dongled.Plugin.HyperXHid;

/// <summary>
/// Presence for the HyperX Cloud III S Wireless, from the dongle's HID event reports.
/// </summary>
/// <remarks>
/// <para>
/// There is no device cache and no last-reported bookkeeping. The open session is a local inside the
/// watch loop, so <see cref="StopAsync"/> has no shared state to clear and cannot race the loop over
/// it. <see cref="IProviderContext.ReportPresence"/> is idempotent, so a repeat costs nothing and the
/// host drops it before it reaches a rule.
/// </para>
/// <para>
/// <see cref="Presence.Unknown"/> is never reported. The host records an Unknown but schedules a
/// disconnect return only when Absent arrives from Present, so an Unknown in the middle would overwrite
/// a Present and silently disable the rule. At startup it is redundant anyway: the host's catalogue
/// already defaults to Unknown.
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Constructed by the host through the assembly-level AudioSourceProvider attribute. The rule looks for an object-creation expression naming the type, which reflection never provides.")]
internal sealed class HyperXHidProvider : IAudioSourceProvider
{
    /// <summary>The one source this provider reports on.</summary>
    internal const string SourceId = "hyperx:cloud-iii-s-wireless:any";

    private static readonly AudioSourceDescriptor[] Sources =
    [
        new AudioSourceDescriptor(
            SourceId,
            "HyperX Cloud III S Wireless",
            "any headset paired to the dongle"),
    ];

    private readonly IHeadsetTransport _transport;
    private readonly HyperXHidTimings _timings;
    private readonly HyperXChargingTracker _charging;

    private CancellationTokenSource? _stopping;
    private Task? _watching;
    private int? _lastPercent;

    /// <summary>The constructor the host uses.</summary>
    public HyperXHidProvider()
        : this(new HidSharpHeadsetTransport(), HyperXHidTimings.Default, TimeProvider.System)
    {
    }

    internal HyperXHidProvider(IHeadsetTransport transport, HyperXHidTimings timings, TimeProvider time)
    {
        _transport = transport;
        _timings = timings;
        _charging = new HyperXChargingTracker(time, timings.ChargingStale);
    }

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new(
        Id: "hyperx.cloud3s.hid",
        DisplayName: "HyperX Cloud III S Wireless (HID)",
        Description: "Watches the Cloud III S dongle for the headset powering on and off.",
        IsExperimental: false);

    /// <inheritdoc />
    public Task StartAsync(IProviderContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Linked, not the token itself, so StopAsync can stop the loop whether the host cancelled or
        // simply asked this plugin to stop.
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Published before anything is known about it, so the rules picker lists the headset even while
        // the dongle is unplugged. No presence yet: the loop answers within a scan.
        context.PublishSources(Sources);

        // Declares that this source has a battery before anything is known about its level, so a
        // headset that is switched off is still listed rather than looking like a device with none.
        context.ReportBattery(SourceId, percent: null, ChargeState.Unknown);

        // A dedicated thread, not the pool: every read in this loop blocks, and wrapping blocking reads
        // in async would only move the block onto a pool thread. Started, not awaited - returning the
        // loop's task would hold the host until its start deadline and then be treated as a failure to
        // start. TaskScheduler.Default is explicit because CA2008 requires it.
        _watching = Task.Factory.StartNew(
            () => Watch(context, _stopping.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct)
    {
        if (_stopping is not null)
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }

        if (_watching is not null)
        {
            // Waiting is what makes "released" true rather than merely requested. Bounded by one event
            // read timeout, because a blocked read is what the loop is doing nearly all the time.
            await _watching.ConfigureAwait(false);
            _watching = null;
        }

        _stopping?.Dispose();
        _stopping = null;
    }

    private void Watch(IProviderContext context, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            IHeadsetSession? session = null;
            try
            {
                session = _transport.TryOpen();
                if (session is null)
                {
                    // An absent dongle is an ordinary state, not a failure, so this is not a warning.
                    ForgetHeadset(context);
                    Log(context, ProviderLogLevel.Debug, "No HyperX dongle present; will scan again.");

                    // Returns as soon as the token is cancelled, so StopAsync is not held for the whole
                    // delay.
                    ct.WaitHandle.WaitOne(_timings.NoDongleRetryDelay);
                    continue;
                }

                Log(context, ProviderLogLevel.Debug, "Dongle open; probing the headset.");
                context.ReportPresence(SourceId, ProbePresence(context, session, ct));
                ReadEvents(context, session, ct);

                if (!ct.IsCancellationRequested)
                {
                    // The stream closed under us, so the dongle or the headset went away.
                    ForgetHeadset(context);
                }
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    Log(context, ProviderLogLevel.Warning, $"HID session failed: {ex.Message}", ex);
                    ForgetHeadset(context);
                }
            }
            finally
            {
                session?.Dispose();
            }

            ct.WaitHandle.WaitOne(_timings.SessionRetryDelay);
        }
    }

    /// <summary>
    /// Ask the headset whether it is there. A headset that is switched off or unpaired does not answer,
    /// and waiting for the next unsolicited event could take hours.
    /// </summary>
    private Presence ProbePresence(IProviderContext context, IHeadsetSession session, CancellationToken ct)
    {
        var request = new byte[session.OutputReportLength];
        HeadsetProtocol.WriteBatteryRequest(request);
        session.Write(request);

        var buffer = new byte[session.InputReportLength];
        var deadline = Environment.TickCount64 + (long)_timings.BatteryProbeTimeout.TotalMilliseconds;

        while (Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
        {
            if (!session.TryRead(buffer, _timings.BatteryReadTimeout, out var count))
            {
                return Presence.Absent;
            }

            if (count == 0)
            {
                continue;
            }

            if (HeadsetProtocol.ReadBatteryPercentage(buffer.AsSpan(0, count)) is { } percentage)
            {
                _lastPercent = percentage;
                context.ReportBattery(SourceId, percentage, _charging.Current());
                return Presence.Present;
            }

            // Feeds the tracker alongside the presence branch below, not instead of it: the same
            // report is both a charging edge and a presence statement, and both readings must run.
            if (HeadsetProtocol.ReadChargingEvent(buffer.AsSpan(0, count)) is { } edge)
            {
                _charging.Observe(edge);
            }

            // A connection event that arrives while the probe is waiting is a better answer than the
            // probe itself: it is the dongle stating the current state rather than us inferring it from
            // a battery reply. Discarding it would mean up to a probe-timeout of blindness, and could
            // report Absent immediately after an event said Present.
            if (HeadsetProtocol.ReadPresenceEvent(buffer.AsSpan(0, count)) is { } reported)
            {
                return reported;
            }
        }

        return Presence.Absent;
    }

    /// <summary>
    /// Read the dongle's unsolicited events until the session ends, asking for the battery level
    /// every <see cref="HyperXHidTimings.BatteryPoll"/> while doing so.
    /// </summary>
    /// <remarks>
    /// The poll is the reason this loop exists rather than just the probe. The dongle announces
    /// presence and charging changes but never a level, so without a tick in here the only reading a
    /// session ever takes is its opening probe's - and with the dongle permanently plugged in that is
    /// one reading for the life of the process. The prior art
    /// (<c>HyperXBatteryHID/Devices/HidBatteryMonitor.cs</c>) queries on an interval inside its open
    /// stream for the same reason.
    /// </remarks>
    private void ReadEvents(IProviderContext context, IHeadsetSession session, CancellationToken ct)
    {
        var buffer = new byte[session.InputReportLength];
        var request = new byte[session.OutputReportLength];

        // The probe has just taken a reading, so the first poll is one whole interval away.
        var nextPoll = Environment.TickCount64 + (long)_timings.BatteryPoll.TotalMilliseconds;

        while (!ct.IsCancellationRequested)
        {
            if (Environment.TickCount64 >= nextPoll)
            {
                nextPoll = Environment.TickCount64 + (long)_timings.BatteryPoll.TotalMilliseconds;

                HeadsetProtocol.WriteBatteryRequest(request);
                session.Write(request);

                // Reported now rather than only when a reply arrives, and reported even though the
                // level has not changed: HyperXChargingTracker ages a "charging stopped" edge out on
                // its own clock, and this tick is the only thing that can carry that decay to the
                // host. ReportBattery is idempotent, so a repeat that says the same thing
                // costs nothing.
                context.ReportBattery(SourceId, _lastPercent, _charging.Current());
            }

            if (!session.TryRead(buffer, _timings.EventReadTimeout, out var count))
            {
                return;
            }

            if (count == 0)
            {
                // A read timeout, which is the ordinary case: the dongle speaks only on change, and
                // it is also what paces the poll above.
                continue;
            }

            // The answer to a poll written above. Kept a separate branch from the two below for the
            // same reason they are separate from each other: a report is read for everything it can
            // say, never for the first thing that matches.
            if (HeadsetProtocol.ReadBatteryPercentage(buffer.AsSpan(0, count)) is { } percentage)
            {
                _lastPercent = percentage;
                context.ReportBattery(SourceId, percentage, _charging.Current());
            }

            // Feeds the tracker alongside the presence branch below, not instead of it: the same
            // report is both a charging edge and a presence statement, and both readings must run.
            if (HeadsetProtocol.ReadChargingEvent(buffer.AsSpan(0, count)) is { } edge)
            {
                _charging.Observe(edge);

                // The level has not changed, but what is known about charging has, and the user is
                // looking at an icon that says otherwise until the next poll.
                context.ReportBattery(SourceId, _lastPercent, _charging.Current());
            }

            if (HeadsetProtocol.ReadPresenceEvent(buffer.AsSpan(0, count)) is { } presence)
            {
                if (presence == Presence.Absent)
                {
                    // The headset was switched off while the dongle stayed plugged in. Nothing about
                    // the session has ended, so none of the three paths in Watch will run: without
                    // this, the catalogue keeps the last level and the page reads "not detected,
                    // 72%" - and the moment the headset comes back that stale figure is eligible for
                    // the tray again. ForgetHeadset reports the Absent this branch owes the host.
                    ForgetHeadset(context);
                }
                else
                {
                    context.ReportPresence(SourceId, presence);
                }
            }
        }
    }

    /// <summary>
    /// Say that nothing is known about the headset any more: it is gone, and what was true of it is
    /// no longer worth showing.
    /// </summary>
    /// <remarks>
    /// One helper rather than the same four lines at each site. There are four: no dongle to open, a
    /// session whose stream closed, a session that failed mid-read, and the headset being switched
    /// off inside a session that is otherwise healthy. Reporting Absent is idempotent, so the sites
    /// that could only have been reached from an already-absent state pay nothing for the repeat -
    /// and the alternative, trimming the redundant lines from one of four otherwise identical
    /// blocks, would buy nothing and leave the reader wondering why that one differs.
    /// </remarks>
    private void ForgetHeadset(IProviderContext context)
    {
        context.ReportPresence(SourceId, Presence.Absent);
        _charging.Reset();
        _lastPercent = null;
        context.ReportBattery(SourceId, percent: null, ChargeState.Unknown);
    }

    private static void Log(
        IProviderContext context,
        ProviderLogLevel level,
        string message,
        Exception? exception = null)
    {
        if (context.Logger.IsEnabled(level))
        {
            context.Logger.Log(level, message, exception);
        }
    }
}
