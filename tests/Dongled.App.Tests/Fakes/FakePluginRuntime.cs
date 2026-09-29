using Dongled.App.Services;
using Dongled.Core.Pipeline;

namespace Dongled.App.Tests.Fakes;

internal sealed class FakePluginRuntime : IPluginRuntime
{
    public TaskCompletionSource StartedSource { get; } = new();

    public List<PluginListing> Results { get; } = [];

    public Dictionary<string, ProviderRunState> States { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int ReloadRequests { get; private set; }

    public Task Started => StartedSource.Task;

    public IReadOnlyList<PluginListing> Listings => [.. Results];

    public ProviderRunState? StateOf(string directory) =>
        States.TryGetValue(directory, out var state) ? state : null;

    public void ReloadConfiguration() => ReloadRequests++;
}
