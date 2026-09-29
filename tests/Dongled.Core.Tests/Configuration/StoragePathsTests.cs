using System.IO;
using Dongled.Core.Configuration;
using Xunit;

namespace Dongled.Core.Tests.Configuration;

public sealed class StoragePathsTests
{
    [Fact]
    public void The_plugins_root_sits_beside_the_executable_not_in_application_data()
    {
        // A plugin has to be where its load context can resolve its dependencies from.
        // Config and state are a different kind of per-user data written by different components.
        // Plugins can now be written to as well (by the install feature), but they still sit
        // beside the executable rather than under AppData for these fundamental reasons.
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "Plugins"),
            StoragePaths.PluginsDirectory);

        Assert.DoesNotContain(
            StoragePaths.AppDataDirectory,
            StoragePaths.PluginsDirectory,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_writable_paths_all_live_under_one_per_user_directory()
    {
        Assert.StartsWith(StoragePaths.AppDataDirectory, StoragePaths.ConfigFile, StringComparison.Ordinal);
        Assert.StartsWith(StoragePaths.AppDataDirectory, StoragePaths.StateFile, StringComparison.Ordinal);
        Assert.StartsWith(StoragePaths.AppDataDirectory, StoragePaths.LogDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void Staging_sits_under_the_writable_app_data_directory()
    {
        // Not under the plugins root, deliberately: PluginLoader.LoadAll enumerates every directory
        // there and lists what it finds, so a working folder would show up as a row.
        Assert.Equal(
            Path.Combine(StoragePaths.AppDataDirectory, "staging"),
            StoragePaths.StagingDirectory);
    }
}
