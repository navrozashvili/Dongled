using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dongled.Core.Plugins;
using Xunit;

namespace Dongled.Core.Tests.Plugins;

public sealed class PluginPackageReaderTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(),
        "asw-package-" + Guid.NewGuid().ToString("N"));

    private readonly string _staging;

    public PluginPackageReaderTests()
    {
        _staging = Path.Combine(_scratch, "staging");
        Directory.CreateDirectory(_staging);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void A_real_plugin_zip_is_read_and_hashed_to_the_same_value_the_loader_would_compute()
    {
        var zip = ZipBuilder.FromDirectory(
            Path.Combine(_scratch, "Sample.zip"),
            TemporaryPluginRoot.SampleSourceDirectory);

        Assert.True(PluginPackageReader.TryRead(zip, _staging, PluginApiVersion.Host, out var package, out var failure));
        Assert.Equal(string.Empty, failure);

        using (package)
        {
            Assert.NotNull(package);
            Assert.Equal("Sample", package.DirectoryName);
            Assert.Equal("Dongled.Plugin.Sample", package.AssemblyName);

            // The hash is taken by the real PluginManifest on the real extracted directory, so it is
            // the value the loader will recompute at the destination. Deriving it from zip entries
            // instead would re-block every plugin this button installs.
            Assert.Equal(PluginManifest.Compute(package.ContentPath).Sha256, package.Sha256);
        }
    }

    [Fact]
    public void A_zip_that_wraps_the_plugin_in_one_folder_reads_the_same()
    {
        var zip = ZipBuilder.FromDirectory(
            Path.Combine(_scratch, "Wrapped.zip"),
            TemporaryPluginRoot.SampleSourceDirectory,
            wrapperFolder: "SamplePlugin");

        Assert.True(PluginPackageReader.TryRead(zip, _staging, PluginApiVersion.Host, out var package, out _));

        using (package)
        {
            Assert.NotNull(package);
            Assert.True(File.Exists(Path.Combine(package.ContentPath, "Dongled.Plugin.Sample.dll")));
        }
    }

    [Fact]
    public void The_folder_name_comes_from_the_zip_not_from_the_wrapper_inside_it()
    {
        var zip = ZipBuilder.FromDirectory(
            Path.Combine(_scratch, "ChosenName.zip"),
            TemporaryPluginRoot.SampleSourceDirectory,
            wrapperFolder: "something-else");

        Assert.True(PluginPackageReader.TryRead(zip, _staging, PluginApiVersion.Host, out var package, out _));

        using (package)
        {
            Assert.Equal("ChosenName", package!.DirectoryName);
        }
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "TryRead returning false means package is always null on this path, so there is nothing to dispose; CA2000 cannot see that from the out parameter's type alone.")]
    public void A_zip_with_no_candidate_assembly_is_refused()
    {
        var zip = ZipBuilder.FromFiles(
            Path.Combine(_scratch, "NoDll.zip"),
            new Dictionary<string, byte[]> { ["readme.txt"] = "hello"u8.ToArray() });

        Assert.False(PluginPackageReader.TryRead(zip, _staging, PluginApiVersion.Host, out var package, out var failure));
        Assert.Null(package);
        Assert.Contains("Dongled.Plugin.*.dll", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zip_with_two_candidate_assemblies_is_refused()
    {
        var zip = ZipBuilder.FromFiles(
            Path.Combine(_scratch, "TwoDlls.zip"),
            new Dictionary<string, byte[]>
            {
                ["Dongled.Plugin.A.dll"] = "a"u8.ToArray(),
                ["Dongled.Plugin.B.dll"] = "b"u8.ToArray(),
            });

        Assert.False(PluginPackageReader.TryRead(zip, _staging, PluginApiVersion.Host, out _, out var failure));
        Assert.NotEqual(string.Empty, failure);
    }

    [Fact]
    public void A_candidate_that_is_not_a_managed_assembly_is_refused()
    {
        var zip = ZipBuilder.FromFiles(
            Path.Combine(_scratch, "Native.zip"),
            new Dictionary<string, byte[]> { ["Dongled.Plugin.X.dll"] = "MZ not really"u8.ToArray() });

        Assert.False(PluginPackageReader.TryRead(zip, _staging, PluginApiVersion.Host, out _, out var failure));
        Assert.NotEqual(string.Empty, failure);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "TryRead returning false means package is always null on this path, so there is nothing to dispose; CA2000 cannot see that from the out parameter's type alone.")]
    public void A_plugin_built_against_an_incompatible_sdk_is_refused_with_both_versions_named()
    {
        var zip = ZipBuilder.FromDirectory(
            Path.Combine(_scratch, "Future.zip"),
            TemporaryPluginRoot.SampleSourceDirectory);

        // A host whose major differs from the plugin's SDK reference can never run it, regardless
        // of minor, so this refusal is genuine and does not depend on this repository's current
        // SDK minor being above zero the way a minor-only mismatch would.
        var incompatibleHost = new Version(PluginApiVersion.Host.Major + 1, PluginApiVersion.Host.Minor);

        Assert.False(PluginPackageReader.TryRead(zip, _staging, incompatibleHost, out var package, out var failure));
        Assert.Null(package);
        Assert.Contains(PluginApiVersion.Describe(PluginApiVersion.Host), failure, StringComparison.Ordinal);
        Assert.Contains(PluginApiVersion.Describe(incompatibleHost), failure, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_that_would_escape_the_staging_folder_writes_nothing()
    {
        var zip = ZipBuilder.FromFiles(
            Path.Combine(_scratch, "Escape.zip"),
            new Dictionary<string, byte[]> { ["../escaped.dll"] = "x"u8.ToArray() });

        Assert.False(PluginPackageReader.TryRead(zip, _staging, PluginApiVersion.Host, out _, out var failure));
        Assert.NotEqual(string.Empty, failure);
        Assert.False(File.Exists(Path.Combine(_scratch, "escaped.dll")));
    }

    [Fact]
    public void A_refused_package_leaves_no_staging_folder_behind()
    {
        var zip = ZipBuilder.FromFiles(
            Path.Combine(_scratch, "Junk.zip"),
            new Dictionary<string, byte[]> { ["readme.txt"] = "hello"u8.ToArray() });

        Assert.False(PluginPackageReader.TryRead(zip, _staging, PluginApiVersion.Host, out _, out _));
        Assert.Empty(Directory.GetDirectories(_staging));
    }

    [Fact]
    public void Disposing_a_package_deletes_its_staging_folder()
    {
        var zip = ZipBuilder.FromDirectory(
            Path.Combine(_scratch, "Disposable.zip"),
            TemporaryPluginRoot.SampleSourceDirectory);

        Assert.True(PluginPackageReader.TryRead(zip, _staging, PluginApiVersion.Host, out var package, out _));

        var path = package!.ContentPath;
        Assert.True(Directory.Exists(path));

        package.Dispose();
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void A_file_that_is_not_a_zip_at_all_is_refused_without_throwing()
    {
        var notAZip = Path.Combine(_scratch, "NotAZip.zip");
        File.WriteAllText(notAZip, "this is not an archive");

        Assert.False(PluginPackageReader.TryRead(notAZip, _staging, PluginApiVersion.Host, out _, out var failure));
        Assert.NotEqual(string.Empty, failure);
    }
}
