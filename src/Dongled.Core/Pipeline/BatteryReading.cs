using Dongled.Abstractions;

namespace Dongled.Core.Pipeline;

/// <summary>What is known about one source's battery.</summary>
/// <remarks>
/// A reading existing at all is the signal that the source has a battery. A reading whose
/// <see cref="Percent"/> is null therefore means "this device has a battery and is not telling us
/// the level" — what a switched-off wireless headset looks like, and the case that has to keep the
/// device listed rather than removing it.
/// </remarks>
/// <param name="Percent">Charge remaining 0 to 100, or null when the level is not known.</param>
/// <param name="Charge">Whether it is charging, as the reporting provider resolved it.</param>
public sealed record BatteryReading(int? Percent, ChargeState Charge);
