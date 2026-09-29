using System.Text.Json;
using Dongled.Core.Configuration;

namespace Dongled.App.Tests.Fakes;

/// <summary>
/// An in-memory configuration store that, like the real one, hands out a fresh copy on every load,
/// so a view model cannot pass a test by mutating what the store holds.
/// </summary>
internal sealed class FakeConfigStore : IConfigStore
{
    private AppConfig _stored;

    public FakeConfigStore(AppConfig? initial = null) => _stored = Clone(initial ?? new AppConfig());

    /// <summary>When set, <see cref="Save"/> throws it.</summary>
    public Exception? SaveFailure { get; set; }

    public int SaveCount { get; private set; }

    /// <summary>A copy of what was last saved.</summary>
    public AppConfig Stored => Clone(_stored);

    public AppConfig Load() => Clone(_stored);

    public void Save(AppConfig config)
    {
        if (SaveFailure is not null)
        {
            throw SaveFailure;
        }

        _stored = Clone(config);
        SaveCount++;
    }

    private static AppConfig Clone(AppConfig config) =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;
}
