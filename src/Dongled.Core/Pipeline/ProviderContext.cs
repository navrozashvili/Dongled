using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using Dongled.Abstractions;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Pipeline;

/// <summary>
/// The <see cref="IProviderContext"/> one provider is given. Validates what it publishes and
/// puts it on the engine's queue.
/// </summary>
/// <remarks>
/// <para>
/// Safe to call from any thread, because that is what the SDK promises and because a HID or
/// socket provider reports from a thread the host never created.
/// </para>
/// <para>
/// Writes go through <see cref="ChannelWriter{T}.WriteAsync"/> rather than
/// <see cref="ChannelWriter{T}.TryWrite"/> even though this is a synchronous method. Falling
/// back from a failed <c>TryWrite</c> to a wait would reorder a provider's own reports under
/// load, and per-provider order is part of the published contract. The wait is the documented
/// backpressure: a provider that floods throttles itself instead of losing reports.
/// </para>
/// <para>
/// Complaints about a provider's own data are logged through the host's logger rather than the
/// provider's, so that a plugin configured to record nothing cannot suppress the host's account
/// of why its descriptors were rejected.
/// </para>
/// </remarks>
internal sealed class ProviderContext : IProviderContext
{
    private readonly string _providerId;
    private readonly ChannelWriter<EngineSignal> _writer;
    private readonly TimeProvider _time;
    private readonly ILogger _hostLogger;

    internal ProviderContext(
        string providerId,
        ChannelWriter<EngineSignal> writer,
        IProviderLogger logger,
        TimeProvider time,
        ILogger hostLogger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(hostLogger);

        _providerId = providerId;
        _writer = writer;
        _time = time;
        _hostLogger = hostLogger;
        Logger = logger;
    }

    /// <inheritdoc />
    public IProviderLogger Logger { get; }

    /// <inheritdoc />
    public void PublishSources(IReadOnlyList<AudioSourceDescriptor> sources)
    {
        // A null list is a caller bug rather than a report of nothing: an empty list already
        // means "I have no sources", so there is nothing null could usefully be taken to mean.
        ArgumentNullException.ThrowIfNull(sources);

        var accepted = new List<AudioSourceDescriptor>(sources.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            if (source is null)
            {
                _hostLogger.LogWarning(
                    "Provider {ProviderId} published a null descriptor; it was dropped.",
                    _providerId);
                continue;
            }

            if (string.IsNullOrWhiteSpace(source.SourceId))
            {
                _hostLogger.LogWarning(
                    "Provider {ProviderId} published a descriptor with no source identifier; it was dropped.",
                    _providerId);
                continue;
            }

            // AudioSourceDescriptor documents this: the host rejects a descriptor whose display
            // name is empty and logs it against the publishing provider. One entry is dropped
            // rather than the call refused, so a single malformed descriptor cannot silence a
            // provider's whole catalogue and take its working sources out of the rules picker.
            if (string.IsNullOrWhiteSpace(source.DisplayName))
            {
                _hostLogger.LogWarning(
                    "Provider {ProviderId} published source {SourceId} with no display name; it was dropped. A descriptor needs a name a user can read.",
                    _providerId,
                    source.SourceId);
                continue;
            }

            if (!seen.Add(source.SourceId))
            {
                _hostLogger.LogWarning(
                    "Provider {ProviderId} published source {SourceId} more than once in one set; the later entry was dropped.",
                    _providerId,
                    source.SourceId);
                continue;
            }

            accepted.Add(source);
        }

        Write(new SourcesPublished(_providerId, _time.GetUtcNow(), accepted));
    }

    /// <inheritdoc />
    public void ReportPresence(string sourceId, Presence presence)
    {
        // Dropped rather than thrown. This can be called from a vendor SDK callback, where an
        // exception unwinds into code neither the host nor the plugin author controls.
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            _hostLogger.LogWarning(
                "Provider {ProviderId} reported presence for a blank source identifier; the report was dropped.",
                _providerId);
            return;
        }

        if (!Enum.IsDefined(presence))
        {
            _hostLogger.LogWarning(
                "Provider {ProviderId} reported presence value {Presence} for {SourceId}, which is not one this build defines; the report was dropped.",
                _providerId,
                (int)presence,
                sourceId);
            return;
        }

        Write(new PresenceReported(_providerId, _time.GetUtcNow(), sourceId, presence));
    }

    /// <inheritdoc />
    public void ReportBattery(string sourceId, int? percent, ChargeState charge)
    {
        // Dropped rather than thrown, for the reason ReportPresence drops: this can be called from a
        // vendor SDK callback, where an exception unwinds into code nobody here controls.
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            _hostLogger.LogWarning(
                "Provider {ProviderId} reported a battery level for a blank source identifier; the report was dropped.",
                _providerId);
            return;
        }

        if (percent is < 0 or > 100)
        {
            _hostLogger.LogWarning(
                "Provider {ProviderId} reported battery {Percent}% for {SourceId}, which is not a percentage; the report was dropped.",
                _providerId,
                percent,
                sourceId);
            return;
        }

        if (!Enum.IsDefined(charge))
        {
            _hostLogger.LogWarning(
                "Provider {ProviderId} reported charge state {Charge} for {SourceId}, which is not one this build defines; the report was dropped.",
                _providerId,
                (int)charge,
                sourceId);
            return;
        }

        Write(new BatteryReported(_providerId, _time.GetUtcNow(), sourceId, percent, charge));
    }

    private void Write(EngineSignal signal)
    {
        try
        {
            var write = _writer.WriteAsync(signal, CancellationToken.None);
            if (!write.IsCompletedSuccessfully)
            {
                write.AsTask().GetAwaiter().GetResult();
            }
        }
        catch (ChannelClosedException)
        {
            // The shutdown order closes the queue only after every provider has been stopped, so
            // reaching here means a provider is still reporting from a thread of its own after
            // its StopAsync returned. There is nothing left to deliver the report to, and the SDK
            // says these methods do not throw at the provider, so it is recorded and dropped.
            _hostLogger.LogWarning(
                "Provider {ProviderId} reported {SignalType} after the pipeline closed; the report was dropped.",
                _providerId,
                signal.GetType().Name);
        }
    }
}
