using System.Diagnostics.CodeAnalysis;
using Dongled.Abstractions;

// One attribute per plugin assembly. This is the whole of discovery: the host never scans types and
// never runs a constructor while working out what this assembly is.
[assembly: AudioSourceProvider(typeof(Dongled.Plugin.Logitech.LogitechGhubProvider))]

namespace Dongled.Plugin.Logitech;

/// <summary>
/// Presence from Logitech G HUB's local agent websocket, which is the only way to tell that a device on
/// a receiver or a dock is actually switched on: Windows PnP presence does not change.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Presence.Unknown"/> is never reported. The host records an Unknown but schedules a
/// disconnect return only when Absent arrives from Present, so an Unknown during an outage would
/// overwrite a Present and the Absent that followed would fire no rule. During an outage this provider
/// reports nothing until the grace period has elapsed, and then reports Absent.
/// </para>
/// <para>
/// Publications are deduplicated and presence reports are not, deliberately. Republishing an identical
/// descriptor set on every refresh allocates a list for nothing, whereas
/// <see cref="IProviderContext.ReportPresence"/> is idempotent and the host drops a repeat before it
/// reaches a rule - so there is no per-source "last reported" bookkeeping here.
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Constructed by the host through the assembly-level AudioSourceProvider attribute. The rule looks for an object-creation expression naming the type, which reflection never provides.")]
internal sealed class LogitechGhubProvider : IAudioSourceProvider
{
    private readonly IGhubChannelFactory _channels;
    private readonly LogitechGhubTimings _timings;
    private readonly string _configPath;
    private readonly Dictionary<string, GhubDevice> _devices = new(StringComparer.Ordinal);

    // Ordinal-ignore-case: a source once seen with a battery must keep being
    // recognised as the same source even if the agent's own casing of a signature ever drifted.
    private readonly HashSet<string> _batterySourcesSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _batteryAnsweredThisRound = new(StringComparer.OrdinalIgnoreCase);

    private LogitechGhubOptions _options = LogitechGhubOptions.Default;
    private IReadOnlyList<AudioSourceDescriptor> _published = [];
    private CancellationTokenSource? _stopping;
    private Task? _running;
    private long _messageId;

    /// <summary>The constructor the host uses.</summary>
    public LogitechGhubProvider()
        : this(
            new ClientWebSocketChannelFactory(),
            LogitechGhubTimings.Default,
            LogitechGhubOptions.DefaultConfigPath)
    {
    }

    internal LogitechGhubProvider(
        IGhubChannelFactory channels,
        LogitechGhubTimings timings,
        string configPath)
    {
        _channels = channels;
        _timings = timings;
        _configPath = configPath;
    }

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new(
        Id: "logitech.ghub",
        DisplayName: "Logitech G HUB",
        Description: "Watches G HUB's local agent for Logitech devices becoming active.",
        IsExperimental: false);

    /// <inheritdoc />
    public Task StartAsync(IProviderContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The config is loaded here rather than in the constructor because that is where a logger first
        // exists, and a config problem the user cannot see is worse than one they can.
        _options = LogitechGhubOptions.Load(_configPath, context.Logger);

        _stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // The aggregate source is hand-written rather than derived from a device, so it can be published
        // before the agent has said anything - the picker lists "Any Logitech device" even with G HUB
        // shut down. Device-derived sources follow when the first list arrives.
        _published = GhubCatalogue.Describe([]);
        context.PublishSources(_published);

        // Started, not awaited: returning the loop's task would hold the host until its start deadline
        // and then be treated as a failure to start.
        _running = Task.Run(() => RunAsync(context, _stopping.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct)
    {
        if (_stopping is not null)
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }

        if (_running is not null)
        {
            // No parting Absent: a plugin cannot tell "the user disabled me" from "the host is shutting
            // down", and a disconnect return fired during shutdown would move the user's default device
            // on the way out.
            await _running.ConfigureAwait(false);
            _running = null;
        }

        _stopping?.Dispose();
        _stopping = null;
    }

    private async Task RunAsync(IProviderContext context, CancellationToken ct)
    {
        DateTimeOffset? outageStarted = null;
        var outageReported = false;

        while (!ct.IsCancellationRequested)
        {
            var channel = _channels.Create();
            try
            {
                await channel.ConnectAsync(_options.Url, _options.KeepAliveInterval, ct).ConfigureAwait(false);
                Log(context, ProviderLogLevel.Information, $"Connected to the G HUB agent at {_options.Url}.");

                outageStarted = null;
                outageReported = false;

                await ConverseAsync(context, channel, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log(context, ProviderLogLevel.Warning, $"G HUB connection failed: {ex.Message}", ex);
            }
            finally
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            outageStarted ??= DateTimeOffset.UtcNow;
            if (!outageReported && DateTimeOffset.UtcNow - outageStarted.Value >= _options.DisconnectGrace)
            {
                outageReported = true;
                ReportOutage(context);
            }

            try
            {
                await Task.Delay(_options.ReconnectDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One connection's worth of conversation, from the agent's hello until it closes.</summary>
    private async Task ConverseAsync(IProviderContext context, IGhubChannel channel, CancellationToken ct)
    {
        // The agent sends an unsolicited OPTIONS message on connect. Reading it clears the stream; it
        // carries nothing this plugin needs.
        if (await channel.ReceiveAsync(ct).ConfigureAwait(false) is null)
        {
            Log(context, ProviderLogLevel.Warning, "The G HUB agent closed the connection during the handshake.");
            return;
        }

        await SendAsync(channel, "GET", GhubMessages.DevicesListPath, ct).ConfigureAwait(false);
        await SendAsync(channel, "SUBSCRIBE", GhubMessages.StateChangedPath, ct).ConfigureAwait(false);

        var lastRefresh = DateTimeOffset.UtcNow;
        var receiving = channel.ReceiveAsync(ct);

        while (!ct.IsCancellationRequested && channel.IsOpen)
        {
            // Racing the receive against a tick rather than cancelling it: cancelling a ReceiveAsync
            // to implement a timeout aborts the socket on Windows.
            var tick = Task.Delay(_timings.TickInterval, ct);
            var completed = await Task.WhenAny(receiving, tick).ConfigureAwait(false);

            if (DateTimeOffset.UtcNow - lastRefresh >= _options.RefreshInterval)
            {
                lastRefresh = DateTimeOffset.UtcNow;

                // A periodic re-read recovers from a missed subscription event. It is the whole list
                // every time, which is the agent's only offer.
                await SendAsync(channel, "GET", GhubMessages.DevicesListPath, ct).ConfigureAwait(false);
            }

            if (completed != receiving)
            {
                continue;
            }

            var message = await receiving.ConfigureAwait(false);
            if (message is null)
            {
                return;
            }

            receiving = channel.ReceiveAsync(ct);
            await HandleAsync(context, channel, message, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(IProviderContext context, IGhubChannel channel, string message, CancellationToken ct)
    {
        if (GhubMessages.TryReadBattery(message, out var battery))
        {
            ReportBattery(context, battery!);
            return;
        }

        var update = GhubMessages.TryReadDevices(message, out var devices);
        switch (update)
        {
            case GhubUpdate.FullList:
                _devices.Clear();
                foreach (var device in devices)
                {
                    _devices[device.Id] = device;
                }

                break;

            case GhubUpdate.Delta:
                foreach (var device in devices)
                {
                    _devices[device.Id] = Merge(_devices.GetValueOrDefault(device.Id), device);
                }

                break;

            default:
                // Not a message this plugin acts on. The agent's hello lands here, as does anything
                // malformed, and so does its NO_SUCH_PATH answer to a battery poll - most Logitech
                // devices simply have no battery, so this is the normal case and nothing is logged.
                return;
        }

        Apply(context);

        // Sweep only on a full list, which arrives only in reply to this provider's own GET
        // /devices/list - the bootstrap one and the periodic RefreshInterval re-GET - never as a
        // consequence of a delta. A delta arrives on /devices/state/changed whenever a device
        // connects or disconnects, and the agent sends those in bursts; sweeping on every message
        // would mean "answered since the previous message of any kind", so a second delta arriving
        // before the first delta's re-polled battery reply came back would wipe a perfectly good
        // reading to (null, Unknown). Gating the sweep on FullList ties it to a completed poll round -
        // GETs go out, the agent has a full RefreshInterval to answer them, then the next round's
        // sweep checks - while a burst of deltas in between still re-polls (so a device's battery is
        // never stale for long) but never sweeps.
        await PollBatteryAsync(context, channel, sweep: update == GhubUpdate.FullList, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Map a battery reply back to the source it is about and forward it. The first report for a
    /// source is what declares it battery-capable (see <see cref="IProviderContext.ReportBattery"/>),
    /// so the source id is also remembered here for as long as the process runs.
    /// </summary>
    private void ReportBattery(IProviderContext context, GhubBattery battery)
    {
        // Measured behaviour never produces a reply for an id this provider does not still know
        // about - a battery reply only ever answers a request this same provider made for a device
        // already in the catalogue - but a signature-less or since-forgotten device is handled by
        // simply not reporting rather than by guessing at a source id.
        if (!_devices.TryGetValue(battery.DeviceId, out var device) || device.Signature.Length == 0)
        {
            return;
        }

        var sourceId = GhubCatalogue.SignatureSourceId(device.Signature);
        _batterySourcesSeen.Add(sourceId);
        _batteryAnsweredThisRound.Add(sourceId);
        context.ReportBattery(sourceId, battery.Percent, ChargeStateOf(battery));
    }

    private static ChargeState ChargeStateOf(GhubBattery battery) =>
        battery.FullyCharged ? ChargeState.Full
        : battery.Charging ? ChargeState.Charging
        : ChargeState.Discharging;

    /// <summary>
    /// Poll every currently known device's battery, and - only when <paramref name="sweep"/> is true -
    /// declare any remembered battery source that went unanswered since the previous sweep
    /// <see cref="ChargeState.Unknown"/> rather than letting it vanish from the Battery page.
    /// </summary>
    /// <remarks>
    /// A disconnected device answers <c>NO_SUCH_PATH</c> exactly like one with no battery at all
    /// (measured 2026-07-28), so "no SUCCESS since the last sweep" is the only signal there is that a
    /// device that once had a battery is currently away. Hung off the same device-list refresh the
    /// provider already does: the agent gave no reply at all to a SUBSCRIBE on a battery
    /// path over 25 seconds, so this polls instead of subscribing. <paramref name="sweep"/> is false
    /// for a delta so a burst of them (the agent sends these on every connect/disconnect) cannot sweep
    /// a reading that simply has not had a full round to be re-answered yet; see the call site.
    /// </remarks>
    private async Task PollBatteryAsync(IProviderContext context, IGhubChannel channel, bool sweep, CancellationToken ct)
    {
        if (sweep)
        {
            foreach (var sourceId in _batterySourcesSeen)
            {
                if (!_batteryAnsweredThisRound.Contains(sourceId))
                {
                    context.ReportBattery(sourceId, null, ChargeState.Unknown);
                }
            }

            _batteryAnsweredThisRound.Clear();
        }

        foreach (var device in _devices.Values)
        {
            await SendAsync(channel, "GET", GhubMessages.BatteryPath(device.Id), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fold a state change into what is already known. A delta carries only the fields that changed, so
    /// an empty one means "not mentioned" rather than "now blank" - which is why every field of
    /// <see cref="GhubDevice"/> is empty-not-null when absent.
    /// </summary>
    private static GhubDevice Merge(GhubDevice? previous, GhubDevice next)
    {
        if (previous is null)
        {
            return next;
        }

        return next with
        {
            State = next.State.Length == 0 ? previous.State : next.State,
            DeviceType = next.DeviceType.Length == 0 ? previous.DeviceType : next.DeviceType,
            DeviceModel = next.DeviceModel.Length == 0 ? previous.DeviceModel : next.DeviceModel,
            DisplayName = next.DisplayName.Length == 0 ? previous.DisplayName : next.DisplayName,
            ExtendedDisplayName = next.ExtendedDisplayName.Length == 0
                ? previous.ExtendedDisplayName
                : next.ExtendedDisplayName,
            Signature = next.Signature.Length == 0 ? previous.Signature : next.Signature,
        };
    }

    private void Apply(IProviderContext context)
    {
        var devices = _devices.Values.ToList();
        var descriptors = GhubCatalogue.Describe(devices);

        // AudioSourceDescriptor is a record, so this is value equality over the whole set. Republishing
        // an identical set would allocate for nothing; see the remarks on this type for why presence
        // reports are deliberately not deduplicated the same way.
        if (!_published.SequenceEqual(descriptors))
        {
            _published = descriptors;
            context.PublishSources(descriptors);
        }

        foreach (var (sourceId, presence) in GhubCatalogue.Presences(devices, _options.ConnectedStates))
        {
            context.ReportPresence(sourceId, presence);
        }
    }

    /// <summary>
    /// The agent has been unreachable for longer than the grace period, so everything published is
    /// absent.
    /// </summary>
    /// <remarks>
    /// The descriptor set is deliberately <em>not</em> republished. Shrinking it back to the aggregate
    /// would drop every device from the rules picker while G HUB restarts, and a rule whose source has
    /// vanished is worse than one whose source is merely absent. The device map is cleared so a
    /// reconnect rebuilds from a fresh full list.
    /// </remarks>
    private void ReportOutage(IProviderContext context)
    {
        Log(
            context,
            ProviderLogLevel.Warning,
            "The G HUB agent has been unreachable for longer than the grace period; reporting every Logitech source absent.");

        _devices.Clear();

        foreach (var descriptor in _published)
        {
            context.ReportPresence(descriptor.SourceId, Presence.Absent);
        }
    }

    private Task SendAsync(IGhubChannel channel, string verb, string path, CancellationToken ct) =>
        channel.SendAsync(GhubMessages.Request(verb, path, ++_messageId), ct);

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
