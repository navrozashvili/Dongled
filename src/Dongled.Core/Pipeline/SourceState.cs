using Dongled.Abstractions;

namespace Dongled.Core.Pipeline;

/// <summary>One source a provider publishes, together with whether it is currently connected.</summary>
/// <remarks>
/// The two halves travel together because the Status page needs both and reading them through two
/// calls would let a presence report land in between, producing a row that names one source and
/// shows another's state.
/// </remarks>
/// <param name="Descriptor">What the source is, including the name to display.</param>
/// <param name="Presence">
/// Whether it is connected. <see cref="Abstractions.Presence.Unknown"/> means nothing has reported
/// on it yet, which is a genuine and useful thing to show — "not detected yet" rather than "not
/// detected". It must never be fed back into the engine as a report.
/// </param>
/// <param name="Battery">
/// What is known about its battery, or null if the source has none. Null and a reading whose
/// percentage is null are different: the first means no battery, the second a battery that is not
/// currently reporting.
/// </param>
public sealed record SourceState(
    AudioSourceDescriptor Descriptor,
    Presence Presence,
    BatteryReading? Battery);
