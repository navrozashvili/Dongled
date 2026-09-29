using Dongled.Core.Configuration;

namespace Dongled.Core.Audio;

/// <summary>One Windows playback endpoint.</summary>
/// <param name="Id">
/// Windows endpoint identifier, for example <c>{0.0.0.00000000}.{a1b2…}</c>. Persisted in rules
/// and never shown to a user.
/// </param>
/// <param name="DisplayName">
/// What Windows sound settings calls it. Empty if the property store could not be read, which
/// is rare but possible for an endpoint that is disappearing as it is enumerated.
/// </param>
/// <param name="IsActive">
/// Whether the endpoint is plugged in and enabled. A previously-default device only counts as
/// somewhere to return to if it is active.
/// </param>
/// <param name="IsDefaultMultimedia">Whether it currently holds the Media role.</param>
/// <param name="IsDefaultCommunications">Whether it currently holds the Calls role.</param>
public sealed record AudioEndpoint(
    string Id,
    string DisplayName,
    bool IsActive,
    bool IsDefaultMultimedia,
    bool IsDefaultCommunications);

/// <summary>Which endpoints currently hold the two roles this app manages.</summary>
/// <param name="MultimediaId">Endpoint holding the Media role, or null if none does.</param>
/// <param name="CommunicationsId">Endpoint holding the Calls role, or null if none does.</param>
public readonly record struct DefaultEndpoints(string? MultimediaId, string? CommunicationsId);

/// <summary>A role changed hands.</summary>
public sealed class DefaultChangedEventArgs : EventArgs
{
    /// <param name="role">The role that changed.</param>
    /// <param name="endpointId">The endpoint that now holds it, or null if none does.</param>
    public DefaultChangedEventArgs(AudioRole role, string? endpointId)
    {
        Role = role;
        EndpointId = endpointId;
    }

    /// <summary>The role that changed.</summary>
    public AudioRole Role { get; }

    /// <summary>The endpoint that now holds the role, or null if none does.</summary>
    public string? EndpointId { get; }
}
