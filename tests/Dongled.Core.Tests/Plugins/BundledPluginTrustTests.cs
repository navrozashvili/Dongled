using System.IO;
using System.Text;
using System.Text.Json;
using Dongled.Core.Configuration;
using Dongled.Core.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dongled.Core.Tests.Plugins;

public sealed class BundledPluginTrustTests
{
    [Fact]
    public void A_shipped_plugin_whose_name_and_hash_match_loads_with_no_configuration_entry()
    {
        using var root = new TemporaryPluginRoot();
        var directory = root.AddSample();

        var result = Assert.Single(LoaderTrusting(Shipped("Sample", directory)).LoadAll(root.Path, []));
        using var plugin = result.Plugin;

        Assert.Equal(PluginLoadStatus.Loaded, result.Status);
        Assert.True(result.IsBundled);
        Assert.NotNull(plugin);
    }

    [Fact]
    public void A_shipped_plugin_loads_even_when_an_older_approval_records_another_hash()
    {
        using var root = new TemporaryPluginRoot();
        var directory = root.AddSample();
        var staleApproval = new PluginConfig { Directory = "Sample", Enabled = true, ManifestSha256 = new string('A', 64) };

        var result = Assert.Single(
            LoaderTrusting(Shipped("Sample", directory)).LoadAll(root.Path, [staleApproval]));
        using var plugin = result.Plugin;

        Assert.Equal(PluginLoadStatus.Loaded, result.Status);
    }

    [Fact]
    public void A_shipped_plugin_whose_files_changed_goes_through_approval_as_usual()
    {
        using var root = new TemporaryPluginRoot();
        var directory = root.AddSample();
        var shipped = Shipped("Sample", directory);
        File.WriteAllText(Path.Combine(directory, "planted.txt"), "not shipped");

        var result = Assert.Single(LoaderTrusting(shipped).LoadAll(root.Path, []));

        Assert.Equal(PluginLoadStatus.Unapproved, result.Status);
        Assert.False(result.IsBundled);
        Assert.Null(result.Plugin);
    }

    [Fact]
    public void A_changed_shipped_plugin_with_an_approval_of_its_old_files_is_reported_as_changed()
    {
        using var root = new TemporaryPluginRoot();
        var directory = root.AddSample();
        var shipped = Shipped("Sample", directory);
        var approval = new PluginConfig
        {
            Directory = "Sample",
            Enabled = true,
            ManifestSha256 = PluginManifest.Compute(directory).Sha256,
        };
        File.WriteAllText(Path.Combine(directory, "planted.txt"), "not shipped");

        var result = Assert.Single(LoaderTrusting(shipped).LoadAll(root.Path, [approval]));

        Assert.Equal(PluginLoadStatus.Changed, result.Status);
        Assert.Null(result.Plugin);
    }

    [Fact]
    public void The_same_files_under_another_directory_name_are_not_trusted()
    {
        using var root = new TemporaryPluginRoot();
        var directory = root.AddSample("Renamed");

        var result = Assert.Single(LoaderTrusting(Shipped("Sample", directory)).LoadAll(root.Path, []));

        Assert.Equal(PluginLoadStatus.Unapproved, result.Status);
        Assert.False(result.IsBundled);
    }

    [Fact]
    public void Switching_a_shipped_plugin_off_beats_the_built_in_trust()
    {
        using var root = new TemporaryPluginRoot();
        var directory = root.AddSample();
        var switchedOff = new PluginConfig { Directory = "Sample", Enabled = false };

        var result = Assert.Single(
            LoaderTrusting(Shipped("Sample", directory)).LoadAll(root.Path, [switchedOff]));

        Assert.Equal(PluginLoadStatus.Disabled, result.Status);
        Assert.True(result.IsBundled);
        Assert.Null(result.Plugin);
    }

    [Fact]
    public void An_empty_list_leaves_every_plugin_needing_approval()
    {
        using var root = new TemporaryPluginRoot();
        root.AddSample();

        var result = Assert.Single(LoaderTrusting(BundledPluginTrust.None).LoadAll(root.Path, []));

        Assert.Equal(PluginLoadStatus.Unapproved, result.Status);
        Assert.False(result.IsBundled);
    }

    [Fact]
    public void A_build_that_embeds_no_list_trusts_nothing()
    {
        // Test builds are not release builds, so nothing is embedded in the Core assembly they use.
        Assert.Empty(BundledPluginTrust.Embedded.Entries);
    }

    [Fact]
    public void A_blank_hash_never_matches()
    {
        var trust = new BundledPluginTrust([new BundledPlugin("Sample", string.Empty)]);

        Assert.False(trust.Covers("Sample", string.Empty));
        Assert.False(trust.Covers("Sample", null));
    }

    [Fact]
    public void The_list_the_release_tool_writes_is_read_by_name_and_hash()
    {
        const string json = """
            {
              "formatVersion": 1,
              "plugins": [
                { "name": "HyperXHid", "version": "1.0.0", "sha256": "ABCDEF", "files": [] },
                { "name": "Logitech", "version": "1.2.0", "sha256": "123456" }
              ]
            }
            """;

        var trust = BundledPluginTrust.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(
            [new BundledPlugin("HyperXHid", "ABCDEF"), new BundledPlugin("Logitech", "123456")],
            trust.Entries);
        Assert.True(trust.Covers("hyperxhid", "abcdef"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "plugins": [ { "name": "X" } ] }""")]
    [InlineData("""{ "plugins": [ { "sha256": "AB" } ] }""")]
    public void A_list_that_is_not_the_expected_shape_is_refused(string json) =>
        Assert.Throws<JsonException>(() => BundledPluginTrust.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json))));

    private static PluginLoader LoaderTrusting(BundledPluginTrust trust) =>
        new(NullLogger<PluginLoader>.Instance, trust);

    private static BundledPluginTrust Shipped(string name, string directory) =>
        new([new BundledPlugin(name, PluginManifest.Compute(directory).Sha256)]);
}
