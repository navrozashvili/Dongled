using System.IO;
using Dongled.Core.Plugins;
using Xunit;

namespace Dongled.Core.Tests.Plugins;

public sealed class PluginManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "asw-manifest-" + Guid.NewGuid().ToString("N"));

    public PluginManifestTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A test that deliberately locks a file may still hold it; the temp directory is not
            // worth failing a run over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Two_directories_holding_the_same_files_hash_the_same()
    {
        var a = NewDirectory("a");
        var b = NewDirectory("b");

        Write(a, "Dongled.Plugin.X.dll", "one");
        Write(a, "HidSharp.dll", "two");
        Write(b, "Dongled.Plugin.X.dll", "one");
        Write(b, "HidSharp.dll", "two");

        Assert.Equal(PluginManifest.Compute(a).Sha256, PluginManifest.Compute(b).Sha256);
    }

    [Fact]
    public void The_hash_does_not_depend_on_the_order_the_files_were_created()
    {
        var a = NewDirectory("order-a");
        var b = NewDirectory("order-b");

        Write(a, "zzz.dll", "z");
        Write(a, "aaa.dll", "a");
        Write(b, "aaa.dll", "a");
        Write(b, "zzz.dll", "z");

        Assert.Equal(PluginManifest.Compute(a).Sha256, PluginManifest.Compute(b).Sha256);
    }

    [Fact]
    public void The_file_list_is_ordinal_sorted_which_is_not_the_order_windows_enumerates_in()
    {
        // NTFS returns directory entries in case-insensitive index order, so it yields "a.dll"
        // before "Z.dll" while ordinal ordering puts "Z.dll" first. Without that difference,
        // creation order alone cannot tell whether the sort is there: NTFS hands back the same
        // order either way, and a manifest that merely happened to agree with the file system
        // would break on the first non-NTFS volume.
        var directory = NewDirectory("ordering");
        Write(directory, "a.dll", "lower");
        Write(directory, "Z.dll", "upper");

        Assert.Equal(["Z.dll", "a.dll"], PluginManifest.Compute(directory).Files.Select(file => file.RelativePath));
    }

    [Fact]
    public void Changing_one_byte_of_any_file_changes_the_manifest_hash()
    {
        // Not the main assembly: a dependency beside it. Pinning only the main file would leave
        // its dependencies unpinned.
        var directory = NewDirectory("changed");
        Write(directory, "Dongled.Plugin.X.dll", "main");
        Write(directory, "HidSharp.dll", "dependency");

        var before = PluginManifest.Compute(directory);
        Write(directory, "HidSharp.dll", "dependencx");
        var after = PluginManifest.Compute(directory);

        Assert.NotEqual(before.Sha256, after.Sha256);
        Assert.Equal(["HidSharp.dll"], after.DifferingFiles(before));
    }

    [Fact]
    public void Adding_a_file_changes_the_manifest_hash_and_names_it()
    {
        var directory = NewDirectory("added");
        Write(directory, "Dongled.Plugin.X.dll", "main");

        var before = PluginManifest.Compute(directory);
        Write(directory, "planted.dll", "surprise");
        var after = PluginManifest.Compute(directory);

        Assert.NotEqual(before.Sha256, after.Sha256);
        Assert.Equal(["planted.dll"], after.DifferingFiles(before));
    }

    [Fact]
    public void Removing_a_file_changes_the_manifest_hash_and_names_it()
    {
        var directory = NewDirectory("removed");
        Write(directory, "Dongled.Plugin.X.dll", "main");
        Write(directory, "HidSharp.dll", "dependency");

        var before = PluginManifest.Compute(directory);
        File.Delete(Path.Combine(directory, "HidSharp.dll"));
        var after = PluginManifest.Compute(directory);

        Assert.NotEqual(before.Sha256, after.Sha256);
        Assert.Equal(["HidSharp.dll"], after.DifferingFiles(before));
    }

    [Fact]
    public void Renaming_a_file_changes_the_manifest_hash_even_though_the_bytes_are_the_same()
    {
        var a = NewDirectory("named-a");
        var b = NewDirectory("named-b");

        Write(a, "one.dll", "identical");
        Write(b, "two.dll", "identical");

        Assert.NotEqual(PluginManifest.Compute(a).Sha256, PluginManifest.Compute(b).Sha256);
    }

    [Fact]
    public void Files_in_subdirectories_are_hashed_and_listed_with_a_relative_path()
    {
        var directory = NewDirectory("nested");
        Write(directory, "Dongled.Plugin.X.dll", "main");
        Write(directory, Path.Combine("runtimes", "win-x64", "native", "vendor.dll"), "native");

        var manifest = PluginManifest.Compute(directory);

        Assert.Equal(
            ["Dongled.Plugin.X.dll", "runtimes/win-x64/native/vendor.dll"],
            manifest.Files.Select(file => file.RelativePath));
        Assert.All(manifest.Files, file => Assert.Equal(64, file.Sha256.Length));
    }

    [Fact]
    public void A_hidden_file_is_hashed_like_any_other()
    {
        // The default EnumerationOptions skip Hidden and System, which would leave a hidden DLL
        // unpinned while AssemblyDependencyResolver would still happily load it.
        var directory = NewDirectory("hidden");
        Write(directory, "Dongled.Plugin.X.dll", "main");

        var before = PluginManifest.Compute(directory);

        var planted = Path.Combine(directory, "planted.dll");
        File.WriteAllText(planted, "surprise");
        File.SetAttributes(planted, FileAttributes.Hidden);

        var after = PluginManifest.Compute(directory);

        Assert.NotEqual(before.Sha256, after.Sha256);
        Assert.Contains(after.Files, file => file.RelativePath == "planted.dll");
    }

    [Fact]
    public void A_linked_subdirectory_is_refused_rather_than_walked_into()
    {
        // A junction rather than a symbolic link, because a junction needs no elevation and so
        // this is the form of the check that actually runs everywhere. Without it, the reparse
        // test inside the walk would be unpinned on any machine without Developer Mode.
        var directory = NewDirectory("with-linked-subdirectory");
        Write(directory, "Dongled.Plugin.X.dll", "main");

        var outside = NewDirectory("outside-target");
        Write(outside, "real.dll", "content");

        if (!Junction.TryCreate(Path.Combine(directory, "vendor"), outside))
        {
            Assert.Skip("Creating a junction is not possible on this machine.");
            return;
        }

        var thrown = Assert.Throws<IOException>(() => PluginManifest.Compute(directory));
        Assert.Contains("vendor", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_symbolic_link_to_a_file_is_refused_rather_than_followed()
    {
        var directory = NewDirectory("with-link");
        Write(directory, "Dongled.Plugin.X.dll", "main");

        var outside = NewDirectory("outside");
        Write(outside, "real.dll", "content");

        try
        {
            File.CreateSymbolicLink(
                Path.Combine(directory, "linked.dll"),
                Path.Combine(outside, "real.dll"));
        }
        catch (IOException)
        {
            Assert.Skip("Creating a symbolic link needs Developer Mode or elevation on this machine.");
            return;
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Skip("Creating a symbolic link needs Developer Mode or elevation on this machine.");
            return;
        }

        var thrown = Assert.Throws<IOException>(() => PluginManifest.Compute(directory));
        Assert.Contains("linked.dll", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_directory_that_is_itself_a_link_is_refused()
    {
        var target = NewDirectory("junction-target");
        Write(target, "Dongled.Plugin.X.dll", "main");

        var junction = Path.Combine(_root, "junction");
        if (!Junction.TryCreate(junction, target))
        {
            Assert.Skip("Creating a junction is not possible on this machine.");
            return;
        }

        Assert.Throws<IOException>(() => PluginManifest.Compute(junction));
    }

    [Fact]
    public void A_file_that_cannot_be_read_makes_the_whole_computation_throw()
    {
        // Fail-closed: the caller must never receive a manifest that silently omits a file.
        var directory = NewDirectory("locked");
        Write(directory, "Dongled.Plugin.X.dll", "main");
        Write(directory, "HidSharp.dll", "dependency");

        using var hold = new FileStream(
            Path.Combine(directory, "HidSharp.dll"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        Assert.Throws<IOException>(() => PluginManifest.Compute(directory));
    }

    [Fact]
    public void A_directory_that_does_not_exist_throws_rather_than_hashing_nothing()
    {
        // An empty manifest would otherwise have a stable hash that a config entry could record,
        // making a deleted plugin directory "approved".
        Assert.Throws<DirectoryNotFoundException>(
            () => PluginManifest.Compute(Path.Combine(_root, "absent")));
    }

    [Fact]
    public void Matches_compares_case_insensitively_and_refuses_a_blank_record()
    {
        var directory = NewDirectory("matches");
        Write(directory, "Dongled.Plugin.X.dll", "main");

        var manifest = PluginManifest.Compute(directory);

        Assert.True(manifest.Matches(manifest.Sha256));
        Assert.True(manifest.Matches(manifest.Sha256.ToLowerInvariant()));
        Assert.False(manifest.Matches(null));
        Assert.False(manifest.Matches(""));
        Assert.False(manifest.Matches("   "));
        Assert.False(manifest.Matches(new string('0', 64)));
    }

    private static void Write(string directory, string relativePath, string content)
    {
        var full = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string NewDirectory(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
