using System.Collections.Generic;
using Dongled.Abstractions;

namespace Dongled.Core.Pipeline;

/// <summary>
/// Anything the switching engine's single consumer processes.
/// </summary>
/// <remarks>
/// The engine drains one bounded channel. This type sits one level above
/// <see cref="ProviderSignal"/> because the host itself has to put things on the same queue — a stabilization delay elapsing, startup resolution finishing, configuration being
/// reloaded. Those are not reports from a provider and giving them a provider identifier would
/// be a lie, but they must travel the same queue: the whole point of the single consumer is that
/// exactly one thread mutates presence, the catalogue and the pending returns, and a host event
/// that arrived by any other route would be a second writer.
/// </remarks>
/// <param name="Timestamp">When the signal was created, from the engine's clock.</param>
public abstract record EngineSignal(DateTimeOffset Timestamp);

/// <summary>Something a provider reported.</summary>
/// <param name="ProviderId">The provider that reported it, from its <see cref="ProviderMetadata"/>.</param>
/// <param name="Timestamp">When the report was made.</param>
public abstract record ProviderSignal(string ProviderId, DateTimeOffset Timestamp) : EngineSignal(Timestamp);

/// <summary>
/// A provider declared the complete set of sources it can report on. Replaces whatever that
/// provider published before; an empty set means it has nothing.
/// </summary>
/// <param name="ProviderId">The publishing provider.</param>
/// <param name="Timestamp">When the report was made.</param>
/// <param name="Sources">The complete set, already validated by the provider context.</param>
public sealed record SourcesPublished(
    string ProviderId,
    DateTimeOffset Timestamp,
    IReadOnlyList<AudioSourceDescriptor> Sources) : ProviderSignal(ProviderId, Timestamp);

/// <summary>A provider reported whether one source is present.</summary>
/// <param name="ProviderId">The reporting provider.</param>
/// <param name="Timestamp">When the report was made.</param>
/// <param name="SourceId">The source it is about.</param>
/// <param name="Presence">What it reported.</param>
public sealed record PresenceReported(
    string ProviderId,
    DateTimeOffset Timestamp,
    string SourceId,
    Presence Presence) : ProviderSignal(ProviderId, Timestamp);

/// <summary>A provider reported one source's battery level, charging state, or both.</summary>
/// <remarks>
/// On the same queue as everything else rather than on a side channel. Battery can never cause a
/// switch, so this costs the consumer a dictionary write and nothing more — but routing it anywhere
/// else would make something other than the consumer a writer of catalogue state, which is the one
/// property the single-consumer design exists to hold.
/// </remarks>
/// <param name="ProviderId">The reporting provider.</param>
/// <param name="Timestamp">When the report was made.</param>
/// <param name="SourceId">The source it is about.</param>
/// <param name="Percent">Charge remaining 0 to 100, or null when the level is not known.</param>
/// <param name="Charge">Whether it is charging, as the provider resolved it.</param>
public sealed record BatteryReported(
    string ProviderId,
    DateTimeOffset Timestamp,
    string SourceId,
    int? Percent,
    ChargeState Charge) : ProviderSignal(ProviderId, Timestamp);

/// <summary>
/// A source's stabilization delay ran out, so its rules' disconnect behaviour is now due.
/// </summary>
/// <param name="Timestamp">When the delay elapsed.</param>
/// <param name="SourceId">The source whose delay elapsed.</param>
/// <param name="Epoch">
/// Which scheduling this belongs to. A source can disconnect, reconnect and disconnect again
/// faster than the queue drains, which would leave two of these in flight; the consumer applies
/// only the one matching the current scheduling and discards the rest.
/// </param>
public sealed record StabilizationElapsed(DateTimeOffset Timestamp, string SourceId, long Epoch)
    : EngineSignal(Timestamp);

/// <summary>
/// Every source an enabled rule names has a known presence, or the resolution ceiling expired.
/// Startup reconciliation runs when the consumer picks this up.
/// </summary>
/// <param name="Timestamp">When resolution finished.</param>
public sealed record StartupResolutionFinished(DateTimeOffset Timestamp) : EngineSignal(Timestamp);

/// <summary>
/// Configuration changed on disk and the consumer should re-read it. Raised by the UI after it
/// saves, since the UI is the only writer of that file.
/// </summary>
/// <param name="Timestamp">When the reload was requested.</param>
public sealed record ConfigurationReloadRequested(DateTimeOffset Timestamp) : EngineSignal(Timestamp);
