using Dongled.Core.Engine;
using Dongled.Core.Pipeline;

namespace Dongled.App.Tests.Fakes;

internal sealed class FakeSwitchingEngine : ISwitchingEngine
{
    public List<SourceState> Sources { get; } = [];

    public int ReloadRequests { get; private set; }

    public IReadOnlyList<SourceState> SourceStates() => [.. Sources];

    public void RequestConfigurationReload() => ReloadRequests++;
}
