using Dongled.Core.Pipeline;

namespace Dongled.Core.Engine;

/// <summary>
/// The part of <see cref="SwitchingEngine"/> the user interface talks to: reading what sources
/// exist and asking for configuration to be re-read after a save.
/// </summary>
/// <remarks>
/// Separate from the engine so that view models can be tested without starting one.
/// </remarks>
public interface ISwitchingEngine
{
    /// <summary>Every source any provider has published, with its presence and battery.</summary>
    IReadOnlyList<SourceState> SourceStates();

    /// <summary>Ask the engine to re-read configuration. Safe to call from any thread.</summary>
    void RequestConfigurationReload();
}
