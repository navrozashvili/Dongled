using Dongled.Core.Audio;
using Dongled.Core.Configuration;

namespace Dongled.App.Tests.Fakes;

internal sealed class FakeAudioEndpointService : IAudioEndpointService
{
    public event EventHandler<DefaultChangedEventArgs>? DefaultChanged;

    public List<AudioEndpoint> Endpoints { get; } = [];

    public DefaultEndpoints Defaults { get; set; }

    /// <summary>When set, <see cref="Enumerate"/> throws it.</summary>
    public Exception? EnumerateFailure { get; set; }

    public IReadOnlyList<AudioEndpoint> Enumerate() =>
        EnumerateFailure is null ? [.. Endpoints] : throw EnumerateFailure;

    public DefaultEndpoints GetDefaults() => Defaults;

    public bool SetDefault(string endpointId, IReadOnlyCollection<AudioRole> roles) => true;

    public void RaiseDefaultChanged(AudioRole role, string? endpointId) =>
        DefaultChanged?.Invoke(this, new DefaultChangedEventArgs(role, endpointId));
}
