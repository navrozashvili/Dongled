namespace Dongled.Core.Configuration;

/// <summary>
/// Remembers which endpoints were default before a rule switched away from them. The switching
/// engine is the only writer.
/// </summary>
/// <remarks>
/// Reads and writes split the same way <see cref="IConfigStore"/> splits them, and for the same
/// reason. A read never throws, because a machine that cannot make sense of its captured state
/// must still start and still switch; the cost is one forgotten return, which the next capture
/// repairs. A write does throw, because state that silently failed to persist would make a return
/// promised by an enabled rule quietly impossible.
/// </remarks>
public interface IStateStore
{
    /// <summary>
    /// What was default before this source's rule last switched, or null if nothing was recorded.
    /// </summary>
    /// <remarks>
    /// Never throws on account of the file. A state file that is missing, unreadable, malformed,
    /// or written by a newer build reads as no recorded state at all, and the file is left on disk
    /// rather than repaired in place. Matching of <paramref name="sourceId"/> ignores case, because
    /// Windows does not guarantee stable casing for the identifiers this is keyed by.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="sourceId"/> is null, empty, or white space. This is a caller bug rather than
    /// a file problem, so it is not swallowed: no source has a blank identifier, and returning null
    /// would report "nothing was captured" for a question that was never validly asked.
    /// </exception>
    PreviousDefault? GetPrevious(string sourceId);

    /// <summary>
    /// Record what was default before switching to <paramref name="targetDeviceId"/>. A role
    /// already sitting on the target is dropped, so a rule can never restore to the device it
    /// switched away from. If that leaves nothing worth remembering, nothing is recorded.
    /// </summary>
    /// <remarks>
    /// Reads fail soft as <see cref="GetPrevious"/> does, so a capture over an unreadable file
    /// records this one source and abandons whatever the file held. The write does not fail soft.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="sourceId"/> or <paramref name="targetDeviceId"/> is null, empty, or white
    /// space.
    /// </exception>
    /// <exception cref="System.IO.IOException">
    /// The write failed. The file still holds its previous content.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The file is read-only or an ACL denies the write. The file still holds its previous
    /// content. This type does not derive from <see cref="System.IO.IOException"/>, so a caller
    /// that catches only that one will not catch this.
    /// </exception>
    void CapturePrevious(string sourceId, string? multimediaId, string? communicationsId, string targetDeviceId);

    /// <summary>Forget what was recorded for this source.</summary>
    /// <remarks>
    /// Writes only if there was something to remove, so clearing a source that holds nothing
    /// touches no file and cannot fail.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="sourceId"/> is null, empty, or white space.
    /// </exception>
    /// <exception cref="System.IO.IOException">
    /// The write failed. The entry is still recorded.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The file is read-only or an ACL denies the write. The entry is still recorded. This type
    /// does not derive from <see cref="System.IO.IOException"/>, so a caller that catches only that
    /// one will not catch this.
    /// </exception>
    void ClearPrevious(string sourceId);
}
