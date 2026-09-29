namespace Dongled.Core.Configuration;

/// <summary>What the last update check found, kept in <c>state.json</c> so a restart does not re-ask GitHub.</summary>
/// <remarks>
/// Runtime state rather than configuration: the app accumulates it and the user never edits it.
/// Every member is optional, so a state file written before this existed reads as "never checked".
/// </remarks>
public sealed class UpdateCheckState
{
    /// <summary>When a check last reached a conclusion, whether it succeeded or not.</summary>
    public DateTimeOffset? LastCheckedUtc { get; set; }

    /// <summary>Whether that check got an answer from GitHub.</summary>
    public bool LastCheckSucceeded { get; set; }

    /// <summary>The newest release version GitHub reported, as its tag spells it without the <c>v</c>.</summary>
    public string? LatestVersion { get; set; }

    /// <summary>A version whose banner the user closed. A newer release brings the banner back.</summary>
    public string? DismissedVersion { get; set; }
}

/// <summary>Reads and writes <see cref="UpdateCheckState"/>. The update service is the only writer.</summary>
/// <remarks>
/// Implemented by the same store as <see cref="IStateStore"/>, which reads through to disk and
/// serializes every read-modify-write, so neither writer can revert the other's section.
/// </remarks>
public interface IUpdateStateStore
{
    /// <summary>The recorded state, or an empty one. Never throws on account of the file.</summary>
    UpdateCheckState LoadUpdateState();

    /// <summary>Replace the recorded state.</summary>
    /// <exception cref="System.IO.IOException">The write failed.</exception>
    /// <exception cref="UnauthorizedAccessException">The file is read-only or an ACL denies the write.</exception>
    void SaveUpdateState(UpdateCheckState state);
}
