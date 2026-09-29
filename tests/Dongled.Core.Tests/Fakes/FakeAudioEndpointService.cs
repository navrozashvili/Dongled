using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dongled.Core.Audio;
using Dongled.Core.Configuration;

namespace Dongled.Core.Tests.Fakes;

/// <summary>
/// An endpoint service with no audio hardware behind it. The whole Core suite runs in
/// milliseconds because of this and <c>FakeTimeProvider</c>.
/// </summary>
public sealed class FakeAudioEndpointService : IAudioEndpointService
{
    /// <summary>The endpoints this fake reports.</summary>
    public List<AudioEndpoint> Endpoints { get; } = [];

    /// <summary>Which endpoints hold the two roles.</summary>
    public DefaultEndpoints Defaults { get; set; }

    /// <summary>Every <see cref="SetDefault"/> that was attempted and accepted, in order.</summary>
    public List<(string EndpointId, AudioRole[] Roles)> SetCalls { get; } = [];

    /// <summary>Makes <see cref="Enumerate"/> fail, standing in for a dead audio service.</summary>
    public bool ThrowOnEnumerate { get; set; }

    /// <summary>Makes <see cref="GetDefaults"/> fail, standing in for a dead audio service.</summary>
    public bool ThrowOnGetDefaults { get; set; }

    /// <summary>
    /// When true, a <see cref="SetDefault"/> naming an endpoint this fake does not list is
    /// refused, which is what Windows does with an identifier that no longer resolves.
    /// </summary>
    public bool RejectUnknownEndpoints { get; set; }

    /// <inheritdoc />
    public event EventHandler<DefaultChangedEventArgs>? DefaultChanged;

    /// <summary>Add an active endpoint.</summary>
    public void AddActive(string id, string displayName) =>
        Endpoints.Add(new AudioEndpoint(id, displayName, IsActive: true, false, false));

    /// <summary>Add an endpoint that exists but is unplugged or disabled.</summary>
    public void AddInactive(string id, string displayName) =>
        Endpoints.Add(new AudioEndpoint(id, displayName, IsActive: false, false, false));

    /// <summary>Raise <see cref="DefaultChanged"/>, as the real service does from a Windows thread.</summary>
    public void RaiseDefaultChanged(AudioRole role, string? endpointId) =>
        DefaultChanged?.Invoke(this, new DefaultChangedEventArgs(role, endpointId));

    /// <inheritdoc />
    public IReadOnlyList<AudioEndpoint> Enumerate()
    {
        if (ThrowOnEnumerate)
        {
            throw new COMException("Fake audio service failure.", unchecked((int)0x80070005));
        }

        return Endpoints
            .Select(endpoint => endpoint with
            {
                IsDefaultMultimedia = Same(endpoint.Id, Defaults.MultimediaId),
                IsDefaultCommunications = Same(endpoint.Id, Defaults.CommunicationsId),
            })
            .ToList();
    }

    /// <inheritdoc />
    public DefaultEndpoints GetDefaults()
    {
        if (ThrowOnGetDefaults)
        {
            throw new COMException("Fake audio service failure.", unchecked((int)0x80070005));
        }

        return Defaults;
    }

    /// <inheritdoc />
    public bool SetDefault(string endpointId, IReadOnlyCollection<AudioRole> roles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        ArgumentNullException.ThrowIfNull(roles);

        if (roles.Count == 0)
        {
            return true;
        }

        if (RejectUnknownEndpoints && !Endpoints.Any(endpoint => Same(endpoint.Id, endpointId)))
        {
            return false;
        }

        SetCalls.Add((endpointId, [.. roles]));

        foreach (var role in roles)
        {
            Defaults = role switch
            {
                AudioRole.Media => Defaults with { MultimediaId = endpointId },
                AudioRole.Calls => Defaults with { CommunicationsId = endpointId },
                _ => Defaults,
            };

            RaiseDefaultChanged(role, endpointId);
        }

        return true;
    }

    private static bool Same(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
