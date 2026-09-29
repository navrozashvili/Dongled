using Dongled.Core.Configuration;

namespace Dongled.Core.Tests.Fakes;

/// <summary>An <see cref="IConfigStore"/> backed by an in-memory object a test can swap.</summary>
public sealed class StubConfigStore : IConfigStore
{
    private int _loadCount;
    private int _saveCount;

    /// <summary>What <see cref="Load"/> returns.</summary>
    public AppConfig Config { get; set; } = new();

    /// <summary>
    /// How many times the engine has read configuration. Read from the test thread while the
    /// consumer writes it, so it goes through <see cref="System.Threading.Interlocked"/>.
    /// </summary>
    public int LoadCount => Volatile.Read(ref _loadCount);

    /// <summary>
    /// How many times the engine has written configuration, by the same reasoning as
    /// <see cref="LoadCount"/>. <see cref="Load"/> hands back the same instance <see cref="Save"/>
    /// stores, so a consumer that mutates what it loaded and never saves is indistinguishable from
    /// one that saves - except by this counter. For anything whose whole purpose is that a change
    /// reaches the file, that difference is the test.
    /// </summary>
    public int SaveCount => Volatile.Read(ref _saveCount);

    /// <inheritdoc />
    public AppConfig Load()
    {
        Interlocked.Increment(ref _loadCount);
        return Config;
    }

    /// <inheritdoc />
    public void Save(AppConfig config)
    {
        Interlocked.Increment(ref _saveCount);
        Config = config;
    }
}
