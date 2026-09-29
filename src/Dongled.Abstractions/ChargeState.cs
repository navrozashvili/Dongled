namespace Dongled.Abstractions;

/// <summary>
/// Whether a source is running down its battery, filling it, or already full.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the percentage because the two are independently available: a dongle may report a
/// level with no idea whether a cable is attached, and a docked device may report charging with no
/// level. Neither implies the other.
/// </para>
/// <para>
/// <see cref="Unknown"/> is not a failure state and providers should not avoid it. Some hardware
/// reports charging only as a transition — a report when it starts and another when it stops, and
/// nothing in between — so a provider that started after the cable went in genuinely cannot know.
/// Saying so is the correct answer. Resolving a device's quirks into one of these values is the
/// provider's job; the host stores what it is told and infers nothing.
/// </para>
/// </remarks>
public enum ChargeState
{
    /// <summary>
    /// The provider cannot tell, or the device does not say. The zero value deliberately, for the
    /// same reason <see cref="Presence.Unknown"/> is.
    /// </summary>
    Unknown = 0,

    /// <summary>Running on its own battery.</summary>
    Discharging = 1,

    /// <summary>Connected to power and filling.</summary>
    Charging = 2,

    /// <summary>Connected to power and no longer filling.</summary>
    Full = 3,
}
