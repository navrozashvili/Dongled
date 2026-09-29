namespace Dongled.Core.Configuration;

/// <summary>Which Windows endpoint role a rule affects.</summary>
/// <remarks>
/// The numeric values are persisted in configuration and must not be renumbered or reordered;
/// new members go on the end.
/// </remarks>
public enum AudioRole
{
    /// <summary>The multimedia role: general playback.</summary>
    Media = 0,

    /// <summary>The communications role: voice chat and calls.</summary>
    Calls = 1,
}

/// <summary>What to do when a rule's source disconnects.</summary>
/// <remarks>
/// The numeric values are persisted in configuration and must not be renumbered or reordered;
/// new members go on the end.
/// </remarks>
public enum DisconnectMode
{
    /// <summary>Restore whatever was default before, or the fallback device if that is gone.</summary>
    RestorePreviousElseFallback = 0,

    /// <summary>Always switch to the fallback device.</summary>
    AlwaysFallback = 1,

    /// <summary>Restore whatever was default before, and do nothing if that is gone.</summary>
    RestorePrevious = 2,

    /// <summary>Leave the default device alone.</summary>
    DoNothing = 3,
}

/// <summary>The audio source a rule watches.</summary>
public sealed class RuleSource
{
    /// <summary>Opaque source identifier published by a provider. Never displayed.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Last display name seen for this source, so the UI can name it even when no provider
    /// currently publishes it.
    /// </summary>
    public string? LastKnownName { get; set; }
}

/// <summary>The playback device a rule switches to.</summary>
public sealed class RuleTarget
{
    /// <summary>Windows endpoint identifier.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Last friendly name seen for this endpoint, so the UI can show
    /// "Speakers (Realtek) - not currently connected" rather than a raw identifier.
    /// </summary>
    public string? LastKnownName { get; set; }

    /// <summary>
    /// A regular expression on the endpoint's display name. When set, the rule switches to
    /// whichever connected endpoint it matches, preferring <see cref="DeviceId"/> if that still
    /// matches, so the rule survives Windows reissuing the endpoint's identifier. Null or empty
    /// means the rule names <see cref="DeviceId"/> alone.
    /// </summary>
    public string? NamePattern { get; set; }

    /// <summary>
    /// Roles to switch. Both multimedia and communications by default.
    /// </summary>
    public List<AudioRole> Roles { get; set; } = [AudioRole.Media, AudioRole.Calls];
}

/// <summary>What happens when the source disconnects.</summary>
public sealed class DisconnectBehavior
{
    /// <summary>Which disconnect behaviour to apply.</summary>
    public DisconnectMode Mode { get; set; } = DisconnectMode.RestorePreviousElseFallback;

    /// <summary>Endpoint used when the mode calls for a fallback.</summary>
    public string? FallbackDeviceId { get; set; }

    /// <summary>Last friendly name seen for the fallback endpoint.</summary>
    public string? FallbackLastKnownName { get; set; }

    /// <summary>
    /// A regular expression on the fallback endpoint's display name, resolved the way
    /// <see cref="RuleTarget.NamePattern"/> is, with <see cref="FallbackDeviceId"/> as the preferred
    /// match. Null or empty means the fallback is <see cref="FallbackDeviceId"/> alone.
    /// </summary>
    public string? FallbackNamePattern { get; set; }
}

/// <summary>One "when this source connects, make that device default" rule.</summary>
public sealed class Rule
{
    /// <summary>
    /// Stable identifier for this rule, unique within the configuration. Assigned by the UI when
    /// the rule is created, as a <see cref="Guid"/> in "N" format: 32 lowercase hexadecimal
    /// digits with no separators. An empty value means the rule has not been persisted yet.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>User-supplied name, shown in the rules list.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether this rule participates in switching.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The source to watch.</summary>
    public RuleSource Source { get; set; } = new();

    /// <summary>The device to switch to.</summary>
    public RuleTarget Target { get; set; } = new();

    /// <summary>What to do when the source goes away.</summary>
    public DisconnectBehavior OnDisconnect { get; set; } = new();
}
