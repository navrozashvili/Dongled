using System.IO;
using Dongled.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dongled.Core.Tests.Configuration;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ass-cfg-" + Guid.NewGuid().ToString("N"));

    public ConfigStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string ConfigPath => Path.Combine(_directory, "config.json");

    private ConfigStore CreateStore() => new(ConfigPath, NullLogger<ConfigStore>.Instance);

    private void WriteRaw(string json) => File.WriteAllText(ConfigPath, json);

    [Fact]
    public void Loading_when_no_file_exists_returns_defaults()
    {
        var config = CreateStore().Load();

        Assert.Equal(5, config.App.DisconnectStabilizationSeconds);
        Assert.Empty(config.Rules);
    }

    [Fact]
    public void A_full_configuration_round_trips()
    {
        var original = new AppConfig
        {
            App = new AppSettings
            {
                StartWithWindows = true,
                ApplyRulesOnStartup = false,
                DisconnectStabilizationSeconds = 12,
                Theme = AppTheme.Dark,
            },
            Rules =
            [
                new Rule
                {
                    Id = "5f2c9e1a",
                    Name = "Headset",
                    Enabled = true,
                    Source = new RuleSource { Id = "hyperx:cloud-iii-s-wireless:any", LastKnownName = "HyperX Cloud III S Wireless" },
                    Target = new RuleTarget
                    {
                        DeviceId = "{0.0.0.00000000}.{target}",
                        LastKnownName = "Headset (HyperX Cloud III S)",
                        NamePattern = "HyperX Cloud III",
                        Roles = [AudioRole.Media],
                    },
                    OnDisconnect = new DisconnectBehavior
                    {
                        Mode = DisconnectMode.AlwaysFallback,
                        FallbackDeviceId = "{0.0.0.00000000}.{fallback}",
                        FallbackLastKnownName = "Speakers (Realtek)",
                        FallbackNamePattern = "^Speakers",
                    },
                },
            ],
            Plugins =
            [
                new PluginConfig
                {
                    Directory = "HyperXHid",
                    Enabled = true,
                    ManifestSha256 = "9f86d081884c7d65",
                    ApprovedUtc = new DateTimeOffset(2026, 7, 25, 14, 2, 11, TimeSpan.Zero),
                    LogLevel = LogLevel.Debug,
                },
            ],
        };

        var store = CreateStore();
        store.Save(original);
        var loaded = store.Load();

        Assert.True(loaded.App.StartWithWindows);
        Assert.False(loaded.App.ApplyRulesOnStartup);
        Assert.Equal(12, loaded.App.DisconnectStabilizationSeconds);
        Assert.Equal(AppTheme.Dark, loaded.App.Theme);

        var rule = Assert.Single(loaded.Rules);
        Assert.Equal("5f2c9e1a", rule.Id);
        Assert.Equal("hyperx:cloud-iii-s-wireless:any", rule.Source.Id);
        Assert.Equal("HyperX Cloud III S Wireless", rule.Source.LastKnownName);
        Assert.Equal("{0.0.0.00000000}.{target}", rule.Target.DeviceId);
        Assert.Equal(new[] { AudioRole.Media }, rule.Target.Roles);
        Assert.Equal(DisconnectMode.AlwaysFallback, rule.OnDisconnect.Mode);
        Assert.Equal("{0.0.0.00000000}.{fallback}", rule.OnDisconnect.FallbackDeviceId);
        Assert.Equal("HyperX Cloud III", rule.Target.NamePattern);
        Assert.Equal("^Speakers", rule.OnDisconnect.FallbackNamePattern);

        var plugin = Assert.Single(loaded.Plugins);
        Assert.Equal("HyperXHid", plugin.Directory);
        Assert.True(plugin.Enabled);
        Assert.Equal(LogLevel.Debug, plugin.LogLevel);
    }

    [Fact]
    public void Enums_are_written_by_name_not_by_number()
    {
        // A numeric enum in a hand-edited file is unreadable, and inserting a member later
        // would silently change what an existing file means.
        var store = CreateStore();
        store.Save(new AppConfig
        {
            App = new AppSettings { Theme = AppTheme.Dark },
            Rules = [new Rule { OnDisconnect = new DisconnectBehavior { Mode = DisconnectMode.DoNothing } }],
        });

        var json = File.ReadAllText(ConfigPath);

        Assert.Contains("\"Dark\"", json, StringComparison.Ordinal);
        Assert.Contains("\"DoNothing\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Corrupt_json_falls_back_to_defaults_instead_of_throwing()
    {
        WriteRaw("{ this is not json");

        var config = CreateStore().Load();

        Assert.Equal(5, config.App.DisconnectStabilizationSeconds);
        Assert.Empty(config.Rules);
    }

    [Fact]
    public void A_corrupt_file_is_left_on_disk_for_inspection()
    {
        // Replacing it immediately would destroy the evidence a user needs to recover
        // hand-written rules. The next Save overwrites it, which is the user's choice.
        WriteRaw("{ this is not json");

        CreateStore().Load();

        Assert.Equal("{ this is not json", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void A_file_containing_only_null_falls_back_to_defaults()
    {
        WriteRaw("null");

        Assert.Equal(5, CreateStore().Load().App.DisconnectStabilizationSeconds);
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        WriteRaw("""
        {
          "schemaVersion": 1,
          "somethingFromAFutureVersion": { "nested": [1, 2, 3] },
          "app": { "disconnectStabilizationSeconds": 7, "unknownSetting": true }
        }
        """);

        Assert.Equal(7, CreateStore().Load().App.DisconnectStabilizationSeconds);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(7, 7)]
    [InlineData(300, 300)]
    [InlineData(4000, 300)]
    public void The_stabilization_delay_is_clamped_to_a_sane_range(int written, int expected)
    {
        WriteRaw($$"""{ "app": { "disconnectStabilizationSeconds": {{written}} } }""");

        Assert.Equal(expected, CreateStore().Load().App.DisconnectStabilizationSeconds);
    }

    [Fact]
    public void Null_collections_and_nested_objects_are_replaced_with_empty_ones()
    {
        // Guards every later consumer against a null dereference on a hand-edited file.
        WriteRaw("""
        {
          "app": null,
          "rules": [ { "id": "r1", "source": null, "target": null, "onDisconnect": null } ],
          "plugins": null
        }
        """);

        var config = CreateStore().Load();

        Assert.NotNull(config.App);
        Assert.NotNull(config.Plugins);
        var rule = Assert.Single(config.Rules);
        Assert.NotNull(rule.Source);
        Assert.NotNull(rule.Target);
        Assert.NotNull(rule.OnDisconnect);
        Assert.NotNull(rule.Target.Roles);
    }

    [Fact]
    public void Null_entries_inside_the_lists_are_dropped()
    {
        // A list entry that is literally null deserializes as a null inside a list every later
        // consumer types as non-nullable. Dropping it is the only sound repair: turning it into
        // a default Rule would invent an enabled rule with an empty source for the engine to act
        // on. Without this, normalizing the rules dereferenced the null and Load threw, which is
        // the one thing it may never do.
        WriteRaw("""{ "rules": [ null ], "plugins": [ null ] }""");

        var config = CreateStore().Load();

        Assert.Empty(config.Rules);
        Assert.Empty(config.Plugins);
    }

    [Fact]
    public void Null_strings_are_repaired_to_empty()
    {
        // System.Text.Json ignores nullable reference annotations, so an explicit null overwrites
        // the property initializer and leaves a null in a field declared non-nullable. Load never
        // dereferences these, so it survives, but a later consumer keying a dictionary by
        // Source.Id would throw a long way from the file that caused it.
        WriteRaw("""
        {
          "rules": [ { "id": null, "name": null, "source": { "id": null }, "target": { "deviceId": null } } ],
          "plugins": [ { "directory": null } ]
        }
        """);

        var config = CreateStore().Load();

        var rule = Assert.Single(config.Rules);
        Assert.Equal(string.Empty, rule.Id);
        Assert.Equal(string.Empty, rule.Name);
        Assert.Equal(string.Empty, rule.Source.Id);
        Assert.Equal(string.Empty, rule.Target.DeviceId);
        Assert.Equal(string.Empty, Assert.Single(config.Plugins).Directory);
    }

    [Fact]
    public void Enum_values_with_no_declared_name_are_repaired()
    {
        // JsonStringEnumConverter reads names but also accepts numbers, so nothing stops a
        // hand-edited file naming a value no member declares. Downstream code has no branch for
        // it, and the save below would write it back out as a bare number.
        WriteRaw("""
        {
          "app": { "theme": 9 },
          "rules": [ { "id": "r1", "target": { "roles": [7, 0] }, "onDisconnect": { "mode": 42 } } ],
          "plugins": [ { "directory": "d", "logLevel": 99 } ]
        }
        """);

        var config = CreateStore().Load();

        Assert.Equal(AppTheme.System, config.App.Theme);
        var rule = Assert.Single(config.Rules);
        Assert.Equal(new[] { AudioRole.Media }, rule.Target.Roles);
        Assert.Equal(DisconnectMode.RestorePreviousElseFallback, rule.OnDisconnect.Mode);
        Assert.Equal(LogLevel.Warning, Assert.Single(config.Plugins).LogLevel);
    }

    [Fact]
    public void An_undefined_role_is_dropped_rather_than_replaced_even_if_it_empties_the_list()
    {
        // Which roles a rule switches is the user's choice. Reinstating the two defaults would
        // turn a file naming one unreadable role into a rule that also seizes the communications
        // endpoint; a rule with no roles switches nothing, which is visible and fixable.
        WriteRaw("""{ "rules": [ { "target": { "roles": [7] } } ] }""");

        Assert.Empty(Assert.Single(CreateStore().Load().Rules).Target.Roles);
    }

    [Fact]
    public void A_repaired_enum_is_not_written_back_as_a_number()
    {
        // The reason the repair belongs on the read path: without it the next save of anything at
        // all puts a bare number into a file whose enum contract is names, and a member added at
        // that value later would silently give the file a meaning nobody chose.
        WriteRaw("""{ "app": { "theme": 9 } }""");

        var store = CreateStore();
        store.Save(store.Load());

        var json = File.ReadAllText(ConfigPath);
        Assert.Contains("\"theme\": \"System\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"theme\": 9", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_over_a_newer_schema_file_copies_it_aside_first()
    {
        // Load leaves a newer file alone, so the user sees defaults; without this, the first save
        // of anything at all would overwrite a configuration they never knew was there.
        var original = """{ "schemaVersion": 99, "app": { "theme": "Dark" } }""";
        WriteRaw(original);

        CreateStore().Save(new AppConfig());

        Assert.Equal(original, File.ReadAllText(ConfigPath + ".newer-v99.bak"));
    }

    [Fact]
    public void Saving_over_a_newer_schema_file_spelled_with_different_casing_still_copies_it_aside()
    {
        // Load reads property names case insensitively, so this file is recognized as newer and
        // left alone there. A case-sensitive check in the save guard would see no version at all
        // and overwrite it: the one combination where the guard is needed and silently absent.
        var original = """{ "SchemaVersion": 99, "app": { "theme": "Dark" } }""";
        WriteRaw(original);

        var store = CreateStore();
        Assert.Equal(5, store.Load().App.DisconnectStabilizationSeconds);

        store.Save(new AppConfig());

        Assert.Equal(original, File.ReadAllText(ConfigPath + ".newer-v99.bak"));
    }

    [Fact]
    public void Saving_over_a_same_or_older_schema_file_leaves_no_backup()
    {
        WriteRaw("""{ "schemaVersion": 1, "app": { "theme": "Dark" } }""");

        CreateStore().Save(new AppConfig());

        Assert.Empty(Directory.GetFiles(_directory, "*.bak"));
    }

    [Fact]
    public void A_file_from_a_newer_schema_version_is_treated_as_unreadable()
    {
        // Guessing at a newer file risks acting on a rule this build misreads.
        WriteRaw("""
        {
          "schemaVersion": 99,
          "app": { "disconnectStabilizationSeconds": 42 }
        }
        """);

        Assert.Equal(5, CreateStore().Load().App.DisconnectStabilizationSeconds);
    }

    [Fact]
    public void A_newer_schema_file_is_left_on_disk_rather_than_overwritten()
    {
        // So downgrading and re-upgrading does not silently destroy a newer configuration.
        var original = """{ "schemaVersion": 99, "app": { "theme": "Dark" } }""";
        WriteRaw(original);

        CreateStore().Load();

        Assert.Equal(original, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void An_older_or_equal_schema_version_is_read_normally()
    {
        WriteRaw("""{ "schemaVersion": 1, "app": { "disconnectStabilizationSeconds": 8 } }""");

        Assert.Equal(8, CreateStore().Load().App.DisconnectStabilizationSeconds);
    }

    [Fact]
    public void Saving_twice_leaves_the_second_value_readable()
    {
        var store = CreateStore();
        store.Save(new AppConfig { App = new AppSettings { DisconnectStabilizationSeconds = 9 } });
        store.Save(new AppConfig { App = new AppSettings { DisconnectStabilizationSeconds = 3 } });

        Assert.Equal(3, store.Load().App.DisconnectStabilizationSeconds);
    }

    [Fact]
    public void The_battery_display_order_defaults_to_empty()
    {
        Assert.Empty(new AppSettings().BatteryDisplayOrder);
    }

    [Fact]
    public void The_battery_display_order_round_trips()
    {
        var store = CreateStore();
        var config = store.Load();
        config.App.BatteryDisplayOrder = ["src:two", "src:one"];

        store.Save(config);

        Assert.Equal(["src:two", "src:one"], store.Load().App.BatteryDisplayOrder);
    }

    [Fact]
    public void A_config_file_written_before_the_battery_order_existed_still_loads()
    {
        WriteRaw("""{"schemaVersion":1,"app":{"theme":2},"rules":[],"plugins":[]}""");

        var config = CreateStore().Load();

        Assert.Empty(config.App.BatteryDisplayOrder);
        Assert.Equal(AppTheme.Dark, config.App.Theme);
    }

    [Fact]
    public void A_null_battery_display_order_is_repaired_to_empty()
    {
        // Same hazard as every other collection in this file: a hand-edited "batteryDisplayOrder":
        // null overwrites the property initializer, and a later consumer iterating the list would
        // throw a NullReferenceException a long way from the file that caused it.
        WriteRaw("""{ "app": { "batteryDisplayOrder": null } }""");

        Assert.Empty(CreateStore().Load().App.BatteryDisplayOrder);
    }

    [Fact]
    public void Null_entries_inside_the_battery_display_order_are_dropped()
    {
        WriteRaw("""{ "app": { "batteryDisplayOrder": [ "src:one", null, "src:two" ] } }""");

        Assert.Equal(["src:one", "src:two"], CreateStore().Load().App.BatteryDisplayOrder);
    }
}
