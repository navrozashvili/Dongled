using System.IO;
using System.Text;
using Dongled.Core.Configuration;
using Dongled.Core.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dongled.Core.Tests.Plugins;

/// <summary>
/// Every one of these asserts the same thing in a different way: when the host cannot be sure, it
/// does not load. A test here that passes because nothing was loaded for the wrong reason is worth
/// no more than one that fails, so each also asserts the status it expects.
/// </summary>
public sealed class PluginLoaderHostileInputTests
{
    // -- Trust decisions ---------------------------------------------------------------------

    [Fact]
    public void An_unknown_directory_is_blocked_because_absence_of_an_entry_means_blocked()
    {
        using var root = new TemporaryPluginRoot();
        root.AddSample();

        var result = Assert.Single(NewLoader().LoadAll(root.Path, []));

        Assert.Equal(PluginLoadStatus.Unapproved, result.Status);
        Assert.Null(result.Plugin);
    }

    [Fact]
    public void A_hash_that_no_longer_matches_is_blocked_and_the_changed_file_can_be_named()
    {
        using var root = new TemporaryPluginRoot();
        var directory = root.AddSample();
        var trust = Approve("Sample", directory);
        var approved = PluginManifest.Compute(directory);

        // A dependency, not the main assembly: pinning only the main file would leave its
        // dependencies unpinned.
        File.WriteAllText(Path.Combine(directory, "Dongled.Abstractions.dll"), "swapped");

        var result = Assert.Single(NewLoader().LoadAll(root.Path, [trust]));

        Assert.Equal(PluginLoadStatus.Changed, result.Status);
        Assert.Null(result.Plugin);
        Assert.NotNull(result.Manifest);
        Assert.Equal(["Dongled.Abstractions.dll"], result.Manifest.DifferingFiles(approved));
    }

    [Fact]
    public void A_plugin_the_user_switched_off_is_not_loaded_even_though_its_hash_matches()
    {
        using var root = new TemporaryPluginRoot();
        var directory = root.AddSample();
        var trust = Approve("Sample", directory);
        trust.Enabled = false;

        var result = Assert.Single(NewLoader().LoadAll(root.Path, [trust]));

        Assert.Equal(PluginLoadStatus.Disabled, result.Status);
        Assert.Null(result.Plugin);
    }

    [Fact]
    public void An_approval_record_with_no_hash_does_not_approve_anything()
    {
        using var root = new TemporaryPluginRoot();
        root.AddSample();

        var trust = new PluginConfig { Directory = "Sample", Enabled = true, ManifestSha256 = null };

        var result = Assert.Single(NewLoader().LoadAll(root.Path, [trust]));

        Assert.Equal(PluginLoadStatus.Changed, result.Status);
        Assert.Null(result.Plugin);
    }

    [Fact]
    public void Hashing_that_throws_blocks_rather_than_loads()
    {
        // The fail-closed case, stated as plainly as it can be: hold a file open against readers
        // so the manifest cannot be computed, and confirm the plugin does not load.
        using var root = new TemporaryPluginRoot();
        var directory = root.AddSample();
        var trust = Approve("Sample", directory);

        using var hold = new FileStream(
            Path.Combine(directory, "Dongled.Plugin.Sample.deps.json"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var result = Assert.Single(NewLoader().LoadAll(root.Path, [trust]));

        Assert.Equal(PluginLoadStatus.Blocked, result.Status);
        Assert.Null(result.Plugin);
    }

    // -- the rest of the corpus --------------------------------------------------------------

    [Fact]
    public void A_zero_byte_file_named_like_a_plugin_is_listed_as_not_a_plugin()
    {
        using var root = new TemporaryPluginRoot();
        var directory = root.AddDirectory("empty");
        File.WriteAllBytes(Path.Combine(directory, "Dongled.Plugin.Empty.dll"), []);

        var result = Assert.Single(NewLoader().LoadAll(root.Path, [Approve("empty", directory)]));

        Assert.Equal(PluginLoadStatus.NotAPlugin, result.Status);
        Assert.Null(result.Plugin);
    }

    [Fact]
    public void A_text_file_renamed_to_dll_is_listed_as_not_a_plugin()
    {
        using var root = new TemporaryPluginRoot();
        var directory = root.AddDirectory("text");
        File.WriteAllText(
            Path.Combine(directory, "Dongled.Plugin.Text.dll"),
            "this is not an assembly, it just has the right extension");

        var result = Assert.Single(NewLoader().LoadAll(root.Path, [Approve("text", directory)]));

        Assert.Equal(PluginLoadStatus.NotAPlugin, result.Status);
        Assert.Null(result.Plugin);
    }

    [Fact]
    public void A_native_library_named_like_a_plugin_is_listed_as_not_a_plugin()
    {
        // The one that a BadImageFormatException check alone would miss: a real native DLL is a
        // valid PE file that simply has no metadata, so nothing throws.
        using var root = new TemporaryPluginRoot();
        var directory = root.AddDirectory("native");
        File.Copy(
            Path.Combine(Environment.SystemDirectory, "winmm.dll"),
            Path.Combine(directory, "Dongled.Plugin.Native.dll"));

        var result = Assert.Single(NewLoader().LoadAll(root.Path, [Approve("native", directory)]));

        Assert.Equal(PluginLoadStatus.NotAPlugin, result.Status);
        Assert.Null(result.Plugin);
    }

    [Fact]
    public void A_candidate_assembly_that_cannot_be_opened_blocks()
    {
        using var root = new TemporaryPluginRoot();
        var directory = root.AddSample();
        var trust = Approve("Sample", directory);

        using var hold = new FileStream(
            TemporaryPluginRoot.MainAssembly(directory),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var result = Assert.Single(NewLoader().LoadAll(root.Path, [trust]));

        Assert.Equal(PluginLoadStatus.Blocked, result.Status);
        Assert.Null(result.Plugin);
    }

    [Fact]
    public void A_configuration_entry_naming_a_path_instead_of_a_directory_matches_nothing()
    {
        // PluginConfig.Directory is compared against the names the host enumerated; it is never
        // combined into a path. A traversal in it therefore cannot reach outside the root, it just
        // fails to match anything.
        using var root = new TemporaryPluginRoot();
        var directory = root.AddSample();

        var trust = new PluginConfig
        {
            Directory = @"..\..\Windows\System32",
            Enabled = true,
            ManifestSha256 = PluginManifest.Compute(directory).Sha256,
        };

        var result = Assert.Single(NewLoader().LoadAll(root.Path, [trust]));

        Assert.Equal(PluginLoadStatus.Unapproved, result.Status);
        Assert.Null(result.Plugin);
    }

    [Fact]
    public void A_configuration_entry_naming_the_right_directory_with_different_case_still_matches()
    {
        // Windows directory names are case-insensitive, so a hand-edited config with the wrong
        // case should not silently un-approve a plugin.
        using var root = new TemporaryPluginRoot();
        var directory = root.AddSample();

        var result = Assert.Single(NewLoader().LoadAll(root.Path, [Approve("sAmPlE", directory)]));

        using var plugin = result.Plugin;
        Assert.Equal(PluginLoadStatus.Loaded, result.Status);
    }

    [Fact]
    public void A_plugin_directory_that_is_a_junction_out_of_the_root_is_blocked()
    {
        using var root = new TemporaryPluginRoot();
        using var elsewhere = new TemporaryPluginRoot();
        var real = elsewhere.AddSample("Real");

        var junction = Path.Combine(root.Path, "Sample");
        if (!Junction.TryCreate(junction, real))
        {
            Assert.Skip("Creating a junction is not possible on this machine.");
            return;
        }

        var trust = new PluginConfig
        {
            Directory = "Sample",
            Enabled = true,
            // Whatever hash the real directory has. The point is that it never gets that far.
            ManifestSha256 = PluginManifest.Compute(real).Sha256,
        };

        var result = Assert.Single(NewLoader().LoadAll(root.Path, [trust]));

        Assert.Equal(PluginLoadStatus.Blocked, result.Status);
        Assert.Null(result.Plugin);
    }

    [Fact]
    public void An_entry_point_attribute_naming_a_type_that_does_not_exist_fails_after_being_trusted()
    {
        // Built by patching the type name inside the attribute blob of a real plugin assembly. The
        // replacement is the same length, so no metadata offset moves and the file stays a valid
        // assembly that fails only where it is supposed to.
        using var root = new TemporaryPluginRoot();
        var directory = root.AddSample();
        var assembly = TemporaryPluginRoot.MainAssembly(directory);

        var bytes = File.ReadAllBytes(assembly);
        var original = Encoding.UTF8.GetBytes("Dongled.Plugin.Sample.SampleProvider");
        var replacement = Encoding.UTF8.GetBytes("Dongled.Plugin.Sample.SampleProvideX");

        Assert.Equal(original.Length, replacement.Length);

        var patched = Replace(bytes, original, replacement);
        Assert.True(patched > 0, "The attribute's type name was not found, so this test would pass vacuously.");

        File.WriteAllBytes(assembly, bytes);

        var result = Assert.Single(NewLoader().LoadAll(root.Path, [Approve("Sample", directory)]));

        Assert.Equal(PluginLoadStatus.Failed, result.Status);
        Assert.Null(result.Plugin);

        // Naming the type that could not be resolved is the difference between a message a plugin
        // author can act on and "this assembly declares no provider", which is what the attribute
        // step would have said if the exception had gone unreported.
        Assert.Contains("SampleProvideX", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void One_broken_plugin_does_not_stop_a_good_one_beside_it_from_loading()
    {
        using var root = new TemporaryPluginRoot();
        var good = root.AddSample("Good");
        var broken = root.AddDirectory("Broken");
        File.WriteAllBytes(Path.Combine(broken, "Dongled.Plugin.Broken.dll"), [0, 1, 2, 3]);

        var results = NewLoader().LoadAll(root.Path, [Approve("Good", good)]);

        Assert.Equal(2, results.Count);

        var brokenResult = results.Single(result => result.Directory == "Broken");
        var goodResult = results.Single(result => result.Directory == "Good");

        using var plugin = goodResult.Plugin;

        Assert.Equal(PluginLoadStatus.NotAPlugin, brokenResult.Status);
        Assert.Equal(PluginLoadStatus.Loaded, goodResult.Status);
    }

    private static PluginLoader NewLoader() => new(NullLogger<PluginLoader>.Instance);

    private static PluginConfig Approve(string directory, string pluginPath) => new()
    {
        Directory = directory,
        Enabled = true,
        ManifestSha256 = PluginManifest.Compute(pluginPath).Sha256,
        ApprovedUtc = DateTimeOffset.UtcNow,
    };

    private static int Replace(byte[] haystack, byte[] needle, byte[] replacement)
    {
        var count = 0;

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (!haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                continue;
            }

            replacement.CopyTo(haystack.AsSpan(i, replacement.Length));
            count++;
        }

        return count;
    }
}
