using System.Collections.Generic;
using System.Linq;
using Dongled.Abstractions;

namespace Dongled.Core.Pipeline;

/// <summary>
/// What sources exist and whether each is present. Owned exclusively by the switching engine's
/// single consumer.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not thread safe, and deliberately internal. Presence changes arrive from provider
/// threads, timer continuations and startup; rather than locking shared collections against all
/// of them, one thread owns this and everything else sends the consumer a signal, so there are no
/// locks anywhere in the policy.
/// </para>
/// <para>
/// Presence is keyed by source identifier and is independent of catalogue membership. A provider
/// that republishes a smaller set has not said anything about whether a source it dropped is
/// connected, so its presence is left as it was. Battery follows the same rule.
/// </para>
/// </remarks>
internal sealed class SourceCatalogue
{
    private readonly Dictionary<string, Dictionary<string, AudioSourceDescriptor>> _byProvider =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Presence> _presence = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, BatteryReading> _battery = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Record the complete set of sources one provider can report on, replacing whatever it
    /// published before.
    /// </summary>
    internal void ReplaceSources(string providerId, IReadOnlyList<AudioSourceDescriptor> sources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(sources);

        var replacement = new Dictionary<string, AudioSourceDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            // The provider context has already dropped null entries, blank identifiers, blank
            // display names and duplicates, so this is a last-writer-wins fallback rather than
            // validation.
            replacement[source.SourceId] = source;
        }

        _byProvider[providerId] = replacement;
    }

    /// <summary>Whether this source is present, as far as anyone has said.</summary>
    internal Presence GetPresence(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        return _presence.GetValueOrDefault(sourceId, Presence.Unknown);
    }

    /// <summary>Record a source's presence, and report what it was before.</summary>
    internal Presence SetPresence(string sourceId, Presence presence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        var previous = _presence.GetValueOrDefault(sourceId, Presence.Unknown);
        _presence[sourceId] = presence;
        return previous;
    }

    /// <summary>What is known about this source's battery, or null if it has none.</summary>
    internal BatteryReading? GetBattery(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        return _battery.GetValueOrDefault(sourceId);
    }

    /// <summary>
    /// Record what a provider said about this source's battery, replacing anything it said before.
    /// </summary>
    /// <remarks>
    /// Kept independently of catalogue membership, exactly as presence is. A provider republishing a
    /// smaller set has not said the dropped source's battery is gone.
    /// </remarks>
    internal void SetBattery(string sourceId, BatteryReading reading)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(reading);

        _battery[sourceId] = reading;
    }

    /// <summary>
    /// Every source any provider currently publishes, one entry per identifier, ordered by the
    /// name a user reads.
    /// </summary>
    internal IReadOnlyList<AudioSourceDescriptor> KnownSources() => _byProvider
        .Values
        .SelectMany(sources => sources.Values)
        .GroupBy(source => source.SourceId, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .OrderBy(source => source.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(source => source.SourceId, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// Every source any provider currently publishes, each with its presence, in the same order as
    /// <see cref="KnownSources"/>.
    /// </summary>
    /// <remarks>
    /// One pass rather than a caller pairing <see cref="KnownSources"/> with
    /// <see cref="GetPresence"/>, so both halves of a row come from the same instant.
    /// </remarks>
    internal IReadOnlyList<SourceState> KnownSourceStates() => KnownSources()
        .Select(source => new SourceState(source, GetPresence(source.SourceId), GetBattery(source.SourceId)))
        .ToList();
}
