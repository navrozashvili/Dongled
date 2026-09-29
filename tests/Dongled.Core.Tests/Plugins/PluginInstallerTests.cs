using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dongled.Core.Configuration;
using Dongled.Core.Plugins;
using Dongled.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dongled.Core.Tests.Plugins;

public sealed class PluginInstallerTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(),
        "asw-installer-" + Guid.NewGuid().ToString("N"));

    private readonly string _plugins;
    private readonly string _staging;
    private readonly StubConfigStore _config = new();
    private readonly HashSet<string> _loaded = new(StringComparer.OrdinalIgnoreCase);

    public PluginInstallerTests()
    {
        _plugins = Path.Combine(_scratch, "Plugins");
        _staging = Path.Combine(_scratch, "staging");
        Directory.CreateDirectory(_plugins);
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

    private PluginInstaller NewInstaller() => new(
        _config,
        _plugins,
        _staging,
        directory => _loaded.Contains(directory),
        NullLogger<PluginInstaller>.Instance);

    /// <summary>
    /// An installer whose staging directory is the very same folder, named by its extended-length
    /// path.
    /// </summary>
    /// <remarks>
    /// <see cref="Directory.Move"/> compares the two path roots as strings before it asks the file
    /// system anything, so <c>\\?\C:\</c> and <c>C:\</c> are different volumes as far as it is
    /// concerned and every move degrades to the copy fallback. That is the only way a test can reach
    /// that fallback without a second disk, and it is worth reaching: staging lives under the user's
    /// profile while the plugins directory sits beside the executable, so a real install crosses
    /// volumes as a matter of course.
    /// </remarks>
    private PluginInstaller NewInstallerWhoseMovesMustCopy() => new(
        _config,
        _plugins,
        @"\\?\" + _staging,
        directory => _loaded.Contains(directory),
        NullLogger<PluginInstaller>.Instance);

    /// <summary>Hold a file open against everything else, the way a scanner or a debugger does.</summary>
    private static FileStream Seize(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.None);

    private string SampleZip(string name) => ZipBuilder.FromDirectory(
        Path.Combine(_scratch, name + ".zip"),
        TemporaryPluginRoot.SampleSourceDirectory);

    /// <summary>
    /// The staged build output of one of the ported plugins, which are the only two genuinely
    /// different plugin assemblies a test can get hold of.
    /// </summary>
    /// <remarks>
    /// Asserted rather than assumed: the directory exists only because the <c>StagePortedPlugins</c>
    /// target copied it there, and a test whose whole point is that two assemblies differ would
    /// otherwise fail with something that says nothing about the staging having gone missing.
    /// </remarks>
    private static string PortedPluginSource(string directoryName)
    {
        var source = TemporaryPluginRoot.PortedPluginSource(directoryName);
        Assert.True(Directory.Exists(source), $"The StagePortedPlugins target did not stage {directoryName} at {source}.");
        return source;
    }

    /// <summary>Copy the built sample into the plugins root, as though it were already installed.</summary>
    private string InstallSampleByHand(string directoryName) =>
        InstallByHand(TemporaryPluginRoot.SampleSourceDirectory, directoryName);

    /// <summary>Copy a built plugin into the plugins root, as though it were already installed.</summary>
    private string InstallByHand(string sourceDirectory, string directoryName)
    {
        var destination = Path.Combine(_plugins, directoryName);
        Directory.CreateDirectory(destination);

        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, source);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }

        return destination;
    }

    [Fact]
    public void A_folder_that_does_not_exist_yet_is_a_fresh_install()
    {
        Assert.True(NewInstaller().TryInspect(SampleZip("Fresh"), out var inspection, out var failure));
        Assert.Equal(string.Empty, failure);

        using (inspection)
        {
            Assert.Equal(PluginInstallOutcome.Fresh, inspection!.Outcome);
            Assert.Equal("Fresh", inspection.DirectoryName);
            Assert.Equal("Dongled.Plugin.Sample", inspection.AssemblyName);
        }
    }

    [Fact]
    public void The_same_files_already_on_disk_are_reported_as_already_installed()
    {
        InstallSampleByHand("Same");

        Assert.True(NewInstaller().TryInspect(SampleZip("Same"), out var inspection, out _));

        using (inspection)
        {
            Assert.Equal(PluginInstallOutcome.AlreadyInstalled, inspection!.Outcome);
        }
    }

    [Fact]
    public void The_same_plugin_with_different_files_is_an_upgrade_that_names_what_changed()
    {
        var installed = InstallSampleByHand("Upgrade");
        File.WriteAllText(Path.Combine(installed, "extra-from-the-old-version.txt"), "stale");

        Assert.True(NewInstaller().TryInspect(SampleZip("Upgrade"), out var inspection, out _));

        using (inspection)
        {
            Assert.Equal(PluginInstallOutcome.Upgrade, inspection!.Outcome);

            // The thing the load-time path cannot do: config.json keeps one combined hash, so at
            // load there is nothing to diff against. Here both manifests are in hand.
            Assert.Contains("extra-from-the-old-version.txt", inspection.DifferingFiles);
        }
    }

    [Fact]
    public void A_different_plugin_wanting_the_same_folder_is_a_conflict()
    {
        // Two separately built plugins, because the assembly name is the only thing that
        // distinguishes "another version of this" from "something else entirely", and it lives in
        // compiled metadata. Nothing a test can do to a copy of one plugin makes it another.
        InstallByHand(PortedPluginSource("HyperXHid"), "Taken");

        var zip = ZipBuilder.FromDirectory(
            Path.Combine(_scratch, "Taken.zip"),
            PortedPluginSource("Logitech"));

        Assert.True(NewInstaller().TryInspect(zip, out var inspection, out _));

        using (inspection)
        {
            Assert.Equal(PluginInstallOutcome.Conflict, inspection!.Outcome);
            Assert.Equal("Dongled.Plugin.HyperXHid", inspection.InstalledAssemblyName);
            Assert.Contains("Rename", inspection.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_candidate_wearing_the_expected_file_name_is_still_judged_by_its_metadata()
    {
        var installed = InstallByHand(PortedPluginSource("HyperXHid"), "Spoofed");

        // The folder now holds Logitech under HyperXHid's file name: the shape a directory takes
        // when something has been dropped in to pass for the plugin already installed there. The
        // file name is the glob that found it, so it is the one thing that cannot be evidence.
        var candidate = TemporaryPluginRoot.MainAssembly(installed);
        File.Copy(
            Path.Combine(PortedPluginSource("Logitech"), "Dongled.Plugin.Logitech.dll"),
            candidate,
            overwrite: true);

        var zip = ZipBuilder.FromDirectory(
            Path.Combine(_scratch, "Spoofed.zip"),
            PortedPluginSource("HyperXHid"));

        Assert.True(NewInstaller().TryInspect(zip, out var inspection, out _));

        using (inspection)
        {
            Assert.Equal(PluginInstallOutcome.Conflict, inspection!.Outcome);
            Assert.Equal("Dongled.Plugin.Logitech", inspection.InstalledAssemblyName);
        }
    }

    [Fact]
    public void An_upgrade_of_a_running_plugin_is_flagged_so_the_dialog_can_say_restart_first()
    {
        InstallSampleByHand("Running");
        File.WriteAllText(Path.Combine(_plugins, "Running", "changed.txt"), "different");
        _loaded.Add("Running");

        Assert.True(NewInstaller().TryInspect(SampleZip("Running"), out var inspection, out _));

        using (inspection)
        {
            Assert.Equal(PluginInstallOutcome.Upgrade, inspection!.Outcome);
            Assert.True(inspection.IsInstalledPluginLoaded);
        }
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "TryInspect returning false means inspection is always null on this path, so there is nothing to dispose; CA2000 cannot see that from the out parameter's type alone.")]
    public void A_zip_that_is_not_a_plugin_never_reaches_classification()
    {
        var zip = ZipBuilder.FromFiles(
            Path.Combine(_scratch, "Junk.zip"),
            new Dictionary<string, byte[]> { ["readme.txt"] = "hello"u8.ToArray() });

        Assert.False(NewInstaller().TryInspect(zip, out var inspection, out var failure));
        Assert.Null(inspection);
        Assert.NotEqual(string.Empty, failure);
    }

    [Fact]
    public void A_fresh_install_lands_the_files_and_returns_their_hash()
    {
        var installer = NewInstaller();
        Assert.True(installer.TryInspect(SampleZip("Landing"), out var inspection, out _));

        using (inspection)
        {
            var result = installer.Install(inspection!);

            Assert.True(result.Succeeded);
            Assert.Equal("Landing", result.DirectoryName);

            var destination = Path.Combine(_plugins, "Landing");
            Assert.True(File.Exists(Path.Combine(destination, "Dongled.Plugin.Sample.dll")));

            Assert.Equal(PluginManifest.Compute(destination).Sha256, result.Sha256);
        }
    }

    [Fact]
    public void Installing_grants_no_trust_of_its_own()
    {
        // A record left behind by a plugin the user deleted from Explorer, which is the one shape a
        // fresh install can meet an approval it never earned in: same folder name, hash of files
        // that are gone. Carrying it forward would let a zip of anything load unapproved.
        _config.Config.Plugins.Add(new PluginConfig
        {
            Directory = "Untrusted",
            Enabled = true,
            ManifestSha256 = "0F1E2D3C4B5A69788796A5B4C3D2E1F00F1E2D3C4B5A69788796A5B4C3D2E1F0",
            ApprovedUtc = DateTimeOffset.UnixEpoch,
        });

        var installer = NewInstaller();
        Assert.True(installer.TryInspect(SampleZip("Untrusted"), out var inspection, out _));

        using (inspection)
        {
            Assert.True(installer.Install(inspection!).Succeeded);
        }

        // The whole point. Files on disk, and nothing in config.json that would let them load.
        Assert.DoesNotContain(
            _config.Config.Plugins,
            plugin => string.Equals(plugin.Directory, "Untrusted", StringComparison.OrdinalIgnoreCase)
                && (plugin.Enabled || plugin.ManifestSha256 is not null));
    }

    [Fact]
    public void An_upgrade_replaces_the_files_and_clears_the_approval_that_covered_the_old_ones()
    {
        var installed = InstallSampleByHand("Replaced");
        File.WriteAllText(Path.Combine(installed, "old.txt"), "stale");

        _config.Config.Plugins.Add(new PluginConfig
        {
            Directory = "Replaced",
            Enabled = true,
            ManifestSha256 = PluginManifest.Compute(installed).Sha256,
            ApprovedUtc = DateTimeOffset.UnixEpoch,
        });

        var installer = NewInstaller();
        Assert.True(installer.TryInspect(SampleZip("Replaced"), out var inspection, out _));

        using (inspection)
        {
            Assert.True(installer.Install(inspection!).Succeeded);
        }

        Assert.False(File.Exists(Path.Combine(installed, "old.txt")));

        var entry = _config.Config.Plugins.Single(
            plugin => string.Equals(plugin.Directory, "Replaced", StringComparison.OrdinalIgnoreCase));

        Assert.Null(entry.ManifestSha256);
        Assert.False(entry.Enabled);

        // The stub hands Load the same instance Save stores, so clearing the entry in memory looks
        // identical to clearing and persisting it. Against the real store the difference is whether
        // the approval is still live at the next start, which is the bypass this class exists to
        // prevent, so the write itself is asserted rather than only its effect.
        Assert.NotEqual(0, _config.SaveCount);
    }

    [Fact]
    public void An_upgrade_of_a_running_plugin_refuses_and_changes_nothing()
    {
        var installed = InstallSampleByHand("Busy");
        File.WriteAllText(Path.Combine(installed, "old.txt"), "stale");
        _loaded.Add("Busy");

        var installer = NewInstaller();
        Assert.True(installer.TryInspect(SampleZip("Busy"), out var inspection, out _));

        using (inspection)
        {
            var result = installer.Install(inspection!);

            Assert.False(result.Succeeded);
            Assert.Contains("restart", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        // Its files are open. Deleting them would fail partway and leave a folder that is neither
        // version, so nothing is attempted at all.
        Assert.True(File.Exists(Path.Combine(installed, "old.txt")));
    }

    [Fact]
    public void Installing_a_package_that_is_already_installed_is_a_programming_error()
    {
        InstallSampleByHand("Identical");

        var installer = NewInstaller();
        Assert.True(installer.TryInspect(SampleZip("Identical"), out var inspection, out _));

        using (inspection)
        {
            Assert.Equal(PluginInstallOutcome.AlreadyInstalled, inspection!.Outcome);
            Assert.Throws<InvalidOperationException>(() => installer.Install(inspection));
        }
    }

    [Fact]
    public void A_successful_install_leaves_no_staging_folders_behind()
    {
        var installer = NewInstaller();
        Assert.True(installer.TryInspect(SampleZip("Tidy"), out var inspection, out _));

        using (inspection)
        {
            Assert.True(installer.Install(inspection!).Succeeded);
        }

        Assert.Empty(Directory.GetDirectories(_staging));
    }

    [Fact]
    public void An_install_across_volumes_falls_back_to_copying_and_still_lands_the_files()
    {
        var installer = NewInstallerWhoseMovesMustCopy();
        Assert.True(installer.TryInspect(SampleZip("Copied"), out var inspection, out _));

        using (inspection)
        {
            var result = installer.Install(inspection!);

            Assert.True(result.Succeeded);
            Assert.Equal(PluginManifest.Compute(Path.Combine(_plugins, "Copied")).Sha256, result.Sha256);
        }

        Assert.Empty(Directory.GetDirectories(_staging));
    }

    [Fact]
    public void Removing_deletes_the_folder_and_forgets_the_trust_record_entirely()
    {
        var installed = InstallSampleByHand("Doomed");

        _config.Config.Plugins.Add(new PluginConfig
        {
            Directory = "Doomed",
            Enabled = true,
            ManifestSha256 = PluginManifest.Compute(installed).Sha256,
            ApprovedUtc = DateTimeOffset.UnixEpoch,
        });

        var result = NewInstaller().Remove("Doomed");

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(installed));

        // Removed, not disabled. A kept entry would mean that dropping a folder of the same name
        // back in later could match a hash approved for files the user deliberately deleted.
        Assert.DoesNotContain(
            _config.Config.Plugins,
            plugin => string.Equals(plugin.Directory, "Doomed", StringComparison.OrdinalIgnoreCase));

        // The stub hands Load the same instance Save stores, so dropping the entry in memory looks
        // identical to dropping it and persisting it. Against the real store the difference is
        // whether the approval is still in config.json at the next start, which is the whole point
        // of removing rather than disabling, so the write itself is asserted.
        Assert.NotEqual(0, _config.SaveCount);
    }

    [Fact]
    public void Removing_forgets_a_trust_record_whose_folder_is_already_gone()
    {
        // What a user who deleted the folder in Explorer leaves behind. The approval outlives the
        // files, and a folder of the same name dropped back in later would be matched against a
        // hash the user never approved those files under - so removal has to withdraw the record
        // whether or not there was anything on disk to delete.
        _config.Config.Plugins.Add(new PluginConfig
        {
            Directory = "AlreadyGone",
            Enabled = true,
            ManifestSha256 = "0F1E2D3C4B5A69788796A5B4C3D2E1F00F1E2D3C4B5A69788796A5B4C3D2E1F0",
            ApprovedUtc = DateTimeOffset.UnixEpoch,
        });

        Assert.True(NewInstaller().Remove("AlreadyGone").Succeeded);

        Assert.Empty(_config.Config.Plugins);
        Assert.NotEqual(0, _config.SaveCount);
    }

    [Fact]
    public void Removing_a_running_plugin_refuses_and_leaves_it_alone()
    {
        var installed = InstallSampleByHand("Live");
        _loaded.Add("Live");

        var result = NewInstaller().Remove("Live");

        Assert.False(result.Succeeded);
        Assert.Contains("restart", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(installed));
    }

    [Fact]
    public void Removing_a_folder_that_is_not_a_plugin_at_all_works_because_the_page_lists_those_too()
    {
        var junk = Path.Combine(_plugins, "JustSomeFolder");
        Directory.CreateDirectory(junk);
        File.WriteAllText(Path.Combine(junk, "notes.txt"), "leftovers");

        Assert.True(NewInstaller().Remove("JustSomeFolder").Succeeded);
        Assert.False(Directory.Exists(junk));
    }

    [Fact]
    public void Removing_a_folder_whose_files_are_held_open_fails_and_keeps_the_trust_record()
    {
        var installed = InstallSampleByHand("Wedged");

        _config.Config.Plugins.Add(new PluginConfig
        {
            Directory = "Wedged",
            Enabled = true,
            ManifestSha256 = PluginManifest.Compute(installed).Sha256,
            ApprovedUtc = DateTimeOffset.UnixEpoch,
        });

        PluginInstallResult result;

        using (Seize(TemporaryPluginRoot.MainAssembly(installed)))
        {
            result = NewInstaller().Remove("Wedged");
        }

        Assert.False(result.Succeeded);

        // The record is withdrawn only once the files are gone, because the two have to stay in
        // step: a recursive delete removes what it can before reporting what it could not, so what
        // is left here is part of the approved plugin, and it is the recorded hash that stops the
        // loader treating the remains as something to run.
        Assert.Single(_config.Config.Plugins);
        Assert.Equal(0, _config.SaveCount);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("nested/inner")]
    [InlineData("C:/Windows")]
    [InlineData("...")]
    [InlineData("....")]
    [InlineData(".. .")]
    public void Removing_refuses_any_name_that_is_not_a_direct_child_of_the_plugins_root(string name)
    {
        // The first five are refused three times over, so removing any single check in
        // TryResolveChild leaves them green, and a passing run is no evidence that any particular
        // one of the three is doing something. It takes two gone before "." and ".." go red.
        //
        // The last three are the opposite, and they are why this test asserts that the plugins root
        // itself is still there. A component of nothing but dots and spaces normalises away
        // completely, so it resolves to the plugins root with a trailing separator - whose parent is
        // the plugins root, so the containment check accepts it. One check refuses these: the
        // resolved leaf no longer being the name that was asked for. Weaken that and Remove("...")
        // deletes the plugins directory and every plugin in it.
        var outside = Path.Combine(_scratch, "outside");
        Directory.CreateDirectory(outside);
        InstallSampleByHand("Bystander");

        Assert.False(NewInstaller().Remove(name).Succeeded);
        Assert.True(Directory.Exists(outside));
        Assert.True(Directory.Exists(_plugins));
        Assert.True(Directory.Exists(Path.Combine(_plugins, "Bystander")));
    }

    [Fact]
    public void A_plugins_root_given_with_a_trailing_separator_still_removes()
    {
        // Nothing in StoragePaths produces one, but the root is a constructor argument and the only
        // thing checked about it is that it is not blank. Path.GetFullPath keeps a trailing
        // separator, and the containment check compares the root against the parent of a resolved
        // path, which never has one - so an unnormalised root matches nothing and refuses every
        // name, disabling removal altogether while installing carries on working.
        var installed = InstallSampleByHand("Doomed");

        var installer = new PluginInstaller(
            _config,
            _plugins + Path.DirectorySeparatorChar,
            _staging,
            directory => _loaded.Contains(directory),
            NullLogger<PluginInstaller>.Instance);

        Assert.True(installer.Remove("Doomed").Succeeded);
        Assert.False(Directory.Exists(installed));
    }

    [Theory]
    [InlineData("Doomed ")]
    [InlineData("Doomed.")]
    [InlineData("Doomed..")]
    [InlineData("Doomed. . ")]
    public void Removing_refuses_a_name_windows_would_quietly_turn_into_a_different_folder(string name)
    {
        // Windows trims trailing spaces and dots off a path component, so every one of these
        // resolves to Plugins\Doomed: a direct child of the plugins root, which is what the
        // containment check asks about and all it asks about. Nothing on disk can be named this
        // way, so a caller asking for one is asking for a folder that does not exist - and what it
        // must not get instead is a different folder deleted. Note that "Doomed.." is not the ".."
        // any traversal check looks for; it is an ordinary name that normalisation shortens.
        var installed = InstallSampleByHand("Doomed");

        Assert.False(NewInstaller().Remove(name).Succeeded);
        Assert.True(Directory.Exists(installed));
    }

    [Theory]
    [InlineData("Doomed:stream")]
    [InlineData("Doo*med")]
    [InlineData("Doo|med")]
    [InlineData("Doo?med")]
    public void Removing_refuses_a_name_that_is_not_a_file_name_at_all(string name)
    {
        // These resolve cleanly to a direct child of the plugins root on this runtime - the
        // containment check sees nothing wrong with them - and no folder can carry such a name, so
        // the only thing accepting one could ever produce is a removal that reports success having
        // deleted nothing while withdrawing a trust record. The colon is the pointed case: on
        // Windows it names an alternate data stream of a directory that may well exist.
        Assert.False(NewInstaller().Remove(name).Succeeded);
    }

    [Fact]
    public void Removing_a_name_that_cannot_be_resolved_to_a_path_refuses_rather_than_throwing()
    {
        // Resolving a name is where a Try method meets input that no amount of comparing gets it
        // past: Path.GetFullPath rejects a null character outright and refuses to normalise a
        // component this long. Neither failure is an IOException or an UnauthorizedAccessException,
        // so an unguarded call does not return false - it leaves Remove entirely and arrives at the
        // Plugins page as an unhandled exception.
        var installer = NewInstaller();

        Assert.False(installer.Remove("Doo\0med").Succeeded);
        Assert.False(installer.Remove(new string('x', 33000)).Succeeded);
    }

    [Fact]
    public void Sweeping_clears_whatever_a_crash_left_in_staging()
    {
        Directory.CreateDirectory(Path.Combine(_staging, "package-leftover"));
        Directory.CreateDirectory(Path.Combine(_staging, "displaced-leftover"));

        // A loose file, which nothing writes here today. The point is the invariant rather than a
        // leak anybody has: staging is swept because everything in it is disposable, and a sweep
        // that empties all but one kind of thing leaves a later change somewhere to accumulate.
        File.WriteAllText(Path.Combine(_staging, "stray.tmp"), "left by something that crashed");

        NewInstaller().SweepStaging();

        Assert.Empty(Directory.GetFileSystemEntries(_staging));
    }

    [Fact]
    public void Sweeping_a_staging_directory_that_was_never_created_does_nothing()
    {
        // The first start on a machine, where nothing has installed anything yet. The sweep runs
        // before anything else touches staging, so the folder not being there is the ordinary case
        // rather than an error.
        Directory.Delete(_staging, recursive: true);

        NewInstaller().SweepStaging();

        Assert.False(Directory.Exists(_staging));
    }
}
