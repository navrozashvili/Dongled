namespace Dongled.Abstractions;

/// <summary>
/// Whether an audio source is currently present.
/// </summary>
public enum Presence
{
    /// <summary>
    /// The provider cannot currently tell. This is the zero value deliberately: a source
    /// nobody has reported on must read as unresolved rather than as absent.
    /// </summary>
    Unknown = 0,

    /// <summary>The source is connected and usable.</summary>
    Present = 1,

    /// <summary>The source is definitively not connected.</summary>
    Absent = 2,
}
