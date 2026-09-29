namespace Dongled.Core.Configuration;

/// <summary>
/// Remembers where the window was. The UI is the only writer.
/// </summary>
/// <remarks>
/// Both halves fail soft, which is where this parts company with <see cref="IConfigStore"/> and
/// <see cref="IStateStore"/>. Those let a failed write reach the caller because a setting or a
/// captured device that silently did not persist changes what the app does. This one holds where a
/// window sits, and it is written at the moment the window is being put away or the process is
/// ending — there is no page left to show an error on, and no behaviour that changes. The cost of a
/// failed write is that the window opens in the default position next time, which is the same thing
/// that happens on a first run.
/// </remarks>
public interface IWindowPlacementStore
{
    /// <summary>
    /// The last recorded placement, or null if none was recorded, the file cannot be read, or it
    /// was written by a newer build.
    /// </summary>
    /// <remarks>
    /// Never throws. The caller is expected to validate what comes back against the displays that
    /// are actually attached: this returns what was written, not what is currently reachable.
    /// </remarks>
    WindowPlacement? Load();

    /// <summary>Record where the window is.</summary>
    /// <remarks>
    /// Never throws. A file this build cannot understand is copied aside before it is replaced, the
    /// same guard the other two stores use.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="placement"/> is null.</exception>
    void Save(WindowPlacement placement);
}
