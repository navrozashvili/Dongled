using Dongled.Core.Configuration;

namespace Dongled.Core.Audio;

/// <summary>
/// Everything the switching engine needs from Windows audio. The seam exists so the switching
/// policy can be tested against a fake.
/// </summary>
/// <remarks>
/// Unlike the two stores, the members here do <em>not</em> fail soft. A transient COM failure
/// that read as "no endpoints exist" would make a previously-default device look permanently
/// gone and trigger a fallback switch the user did not ask for, so the failure is surfaced and
/// the caller decides. The engine's answer is to log and take no action, which leaves the user
/// where they already were.
/// </remarks>
public interface IAudioEndpointService
{
    /// <summary>
    /// Raised when a role changes hands, including when this app is what changed it, and
    /// including changes made in Windows sound settings while the app is running, so the Status
    /// page reflects the truth rather than the last thing the app did.
    /// </summary>
    /// <remarks>
    /// Raised on a thread Windows owns, not on the thread that subscribed and not on the UI
    /// thread. A handler must marshal for itself.
    /// </remarks>
    event EventHandler<DefaultChangedEventArgs>? DefaultChanged;

    /// <summary>
    /// Every playback endpoint in any state, ordered the way the UI wants them: the endpoints
    /// currently holding a role first, then active ones, then by name.
    /// </summary>
    /// <exception cref="Exception">
    /// Windows audio could not be queried. The failure result is translated by
    /// <see cref="System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(int)"/>, so the type
    /// is <see cref="System.Runtime.InteropServices.COMException"/> unless the result maps to a
    /// more precise one such as <see cref="OutOfMemoryException"/>. A caller that must not
    /// misread a failure as "there are no endpoints" therefore has to catch broadly rather than
    /// catch one type.
    /// </exception>
    IReadOnlyList<AudioEndpoint> Enumerate();

    /// <summary>Which endpoints currently hold the Media and Calls roles.</summary>
    /// <exception cref="Exception">
    /// Windows audio could not be queried. The failure result is translated by
    /// <see cref="System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(int)"/>, so the type
    /// is <see cref="System.Runtime.InteropServices.COMException"/> unless the result maps to a
    /// more precise one such as <see cref="OutOfMemoryException"/>. A caller that must not
    /// misread a failure as "there are no endpoints" therefore has to catch broadly rather than
    /// catch one type.
    /// </exception>
    DefaultEndpoints GetDefaults();

    /// <summary>
    /// Give the named roles to this endpoint, and report whether every one of them actually
    /// ended up there.
    /// </summary>
    /// <remarks>
    /// Verified after the fact rather than trusted, because the underlying call can report
    /// success without Windows having switched. An unknown endpoint identifier is refused
    /// without attempting the change. An empty role collection is a no-op that reports success.
    /// </remarks>
    /// <param name="endpointId">Windows endpoint identifier.</param>
    /// <param name="roles">Roles to move. Duplicates and undefined values are ignored.</param>
    /// <exception cref="ArgumentException"><paramref name="endpointId"/> is null, empty, or white space.</exception>
    /// <exception cref="Exception">
    /// Windows audio could not be reached at all. A failure of the change itself is reported as
    /// <see langword="false"/> rather than thrown, so one failed role does not abandon the other. See
    /// <see cref="Enumerate"/> for why the documented type is no narrower than this.
    /// </exception>
    bool SetDefault(string endpointId, IReadOnlyCollection<AudioRole> roles);
}
