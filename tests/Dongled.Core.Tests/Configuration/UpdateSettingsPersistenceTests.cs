using System.IO;
using Dongled.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Dongled.Core.Tests.Configuration;

public sealed class UpdateSettingsPersistenceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "dongled-upd-cfg-" + Guid.NewGuid().ToString("N"));

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));

    public UpdateSettingsPersistenceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string ConfigPath => Path.Combine(_directory, "config.json");

    private string StatePath => Path.Combine(_directory, "state.json");

    private StateStore CreateStateStore() => new(StatePath, _time, NullLogger<StateStore>.Instance);

    [Fact]
    public void Checking_for_updates_is_on_by_default() =>
        Assert.True(new AppConfig().App.CheckForUpdates);

    [Fact]
    public void A_configuration_written_before_the_setting_existed_loads_with_it_on()
    {
        File.WriteAllText(ConfigPath, """
            {
              "schemaVersion": 1,
              "app": { "startWithWindows": true, "theme": "Dark" },
              "rules": []
            }
            """);

        var config = new ConfigStore(ConfigPath, NullLogger<ConfigStore>.Instance).Load();

        Assert.True(config.App.CheckForUpdates);
        Assert.True(config.App.StartWithWindows);
    }

    [Fact]
    public void Turning_it_off_survives_a_reload()
    {
        var store = new ConfigStore(ConfigPath, NullLogger<ConfigStore>.Instance);
        var config = store.Load();
        config.App.CheckForUpdates = false;
        store.Save(config);

        Assert.False(new ConfigStore(ConfigPath, NullLogger<ConfigStore>.Instance).Load().App.CheckForUpdates);
        Assert.Contains("\"checkForUpdates\": false", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 1", File.ReadAllText(ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_state_file_written_before_update_checks_existed_reads_as_never_checked()
    {
        File.WriteAllText(StatePath, """{ "schemaVersion": 1, "previousDefaults": {} }""");

        var state = CreateStateStore().LoadUpdateState();

        Assert.Null(state.LastCheckedUtc);
        Assert.Null(state.LatestVersion);
    }

    [Fact]
    public void The_update_state_round_trips()
    {
        CreateStateStore().SaveUpdateState(new UpdateCheckState
        {
            LastCheckedUtc = _time.GetUtcNow(),
            LastCheckSucceeded = true,
            LatestVersion = "1.0.57",
            DismissedVersion = "1.0.56",
        });

        var loaded = CreateStateStore().LoadUpdateState();

        Assert.Equal(_time.GetUtcNow(), loaded.LastCheckedUtc);
        Assert.True(loaded.LastCheckSucceeded);
        Assert.Equal("1.0.57", loaded.LatestVersion);
        Assert.Equal("1.0.56", loaded.DismissedVersion);
    }

    [Fact]
    public void A_capture_by_the_engine_keeps_the_update_state_and_the_other_way_round()
    {
        var store = CreateStateStore();

        store.SaveUpdateState(new UpdateCheckState { LatestVersion = "1.0.57" });
        store.CapturePrevious("source", "{speakers}", null, "{headset}");
        store.SaveUpdateState(new UpdateCheckState { LatestVersion = "1.0.58" });
        store.ClearPrevious("other");

        Assert.Equal("1.0.58", store.LoadUpdateState().LatestVersion);
        Assert.Equal("{speakers}", store.GetPrevious("source")?.MultimediaId);
    }

    [Fact]
    public void Concurrent_writers_do_not_lose_each_others_sections()
    {
        var store = CreateStateStore();

        Parallel.For(0, 40, i =>
        {
            if (i % 2 == 0)
            {
                store.CapturePrevious($"source-{i}", "{speakers}", null, "{headset}");
            }
            else
            {
                store.SaveUpdateState(new UpdateCheckState { LatestVersion = "1.0.57" });
            }
        });

        Assert.Equal("1.0.57", store.LoadUpdateState().LatestVersion);
        for (var i = 0; i < 40; i += 2)
        {
            Assert.NotNull(store.GetPrevious($"source-{i}"));
        }
    }
}
