using Dongled.Abstractions;

namespace Dongled.Plugins.Tests;

/// <summary>
/// Stands in for the host. Every member is safe to read from the test thread while a provider's own
/// thread is still reporting, because that is the only way these providers can be driven.
/// </summary>
internal sealed class FakeProviderContext : IProviderContext
{
    private readonly List<IReadOnlyList<AudioSourceDescriptor>> _publications = [];
    private readonly List<(string SourceId, Presence Presence)> _presenceReports = [];
    private readonly List<(string SourceId, int? Percent, ChargeState Charge)> _batteryReports = [];
    private readonly object _gate = new();

    public FakeProviderContext(ProviderLogLevel enabled = ProviderLogLevel.Trace)
        => Recorded = new RecordingProviderLogger(enabled);

    /// <summary>The same logger as <see cref="Logger"/>, typed so a test can read what it recorded.</summary>
    public RecordingProviderLogger Recorded { get; }

    /// <inheritdoc />
    public IProviderLogger Logger => Recorded;

    /// <summary>Every published set, oldest first.</summary>
    public IReadOnlyList<IReadOnlyList<AudioSourceDescriptor>> Publications
    {
        get
        {
            lock (_gate)
            {
                return [.. _publications];
            }
        }
    }

    /// <summary>Every presence report, oldest first.</summary>
    public IReadOnlyList<(string SourceId, Presence Presence)> PresenceReports
    {
        get
        {
            lock (_gate)
            {
                return [.. _presenceReports];
            }
        }
    }

    /// <summary>Every battery report the provider made, in order.</summary>
    public IReadOnlyList<(string SourceId, int? Percent, ChargeState Charge)> BatteryReports
    {
        get
        {
            lock (_gate)
            {
                return [.. _batteryReports];
            }
        }
    }

    /// <summary>The most recently published set, or empty if nothing has been published.</summary>
    public IReadOnlyList<AudioSourceDescriptor> LatestSources
    {
        get
        {
            lock (_gate)
            {
                return _publications.Count == 0 ? [] : _publications[^1];
            }
        }
    }

    /// <inheritdoc />
    public void PublishSources(IReadOnlyList<AudioSourceDescriptor> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        // Snapshotted, not stored. IProviderContext says the host may read the list after this returns
        // and that a provider should build a new one each time; keeping the reference would let a
        // provider that recycles one list pass.
        lock (_gate)
        {
            _publications.Add([.. sources]);
        }
    }

    /// <inheritdoc />
    public void ReportPresence(string sourceId, Presence presence)
    {
        lock (_gate)
        {
            _presenceReports.Add((sourceId, presence));
        }
    }

    /// <inheritdoc />
    public void ReportBattery(string sourceId, int? percent, ChargeState charge)
    {
        lock (_gate)
        {
            _batteryReports.Add((sourceId, percent, charge));
        }
    }

    /// <summary>The most recent presence reported for one source, or null if it never was.</summary>
    public Presence? PresenceOf(string sourceId)
    {
        lock (_gate)
        {
            for (var i = _presenceReports.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_presenceReports[i].SourceId, sourceId, StringComparison.Ordinal))
                {
                    return _presenceReports[i].Presence;
                }
            }

            return null;
        }
    }

    /// <summary>Every presence reported for one source, oldest first. The transitions are what matter.</summary>
    public IReadOnlyList<Presence> PresenceHistory(string sourceId) =>
    [
        .. PresenceReports
            .Where(r => string.Equals(r.SourceId, sourceId, StringComparison.Ordinal))
            .Select(r => r.Presence),
    ];
}
