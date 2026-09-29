using System.IO;
using Dongled.Core.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dongled.Core.Tests.Updates;

public sealed class AppFolderSwapTests : IDisposable
{
    private readonly TemporaryDirectory _root = new();

    public AppFolderSwapTests()
    {
        _root.Write(@"app\Dongled.exe", "old exe");
        _root.Write(@"app\Dongled.dll", "old dll");
        _root.Write(@"app\Kept.dll", "not shipped any more");
        _root.Write(@"app\Assets\TrayIcon.ico", "old icon");
        _root.Write(@"app\Plugins\HyperXHid\Dongled.Plugin.HyperXHid.dll", "old plugin");
        _root.Write(@"app\Plugins\HyperXHid\Retired.dll", "a file the new plugin no longer ships");
        _root.Write(@"app\Plugins\MyOwnPlugin\Dongled.Plugin.Mine.dll", "the user's plugin");

        _root.Write(@"new\Dongled.exe", "new exe");
        _root.Write(@"new\Dongled.dll", "new dll");
        _root.Write(@"new\Added.dll", "new file");
        _root.Write(@"new\Assets\TrayIcon.ico", "new icon");
        _root.Write(@"new\Plugins\HyperXHid\Dongled.Plugin.HyperXHid.dll", "new plugin");
        _root.Write(@"new\Plugins\Logitech\Dongled.Plugin.Logitech.dll", "newly bundled plugin");
    }

    public void Dispose() => _root.Dispose();

    private string App => _root.Combine("app");

    private int Apply() => AppFolderSwap.Apply(_root.Combine("new"), App, NullLogger.Instance);

    private string[] BackedUp() =>
        Directory.Exists(_root.Combine(@"app", AppFolderSwap.BackupDirectoryName))
            ? [.. Directory.EnumerateFiles(_root.Combine(@"app", AppFolderSwap.BackupDirectoryName), "*", SearchOption.AllDirectories)]
            : [];

    [Fact]
    public void Every_shipped_file_is_replaced_or_added()
    {
        Assert.Equal(6, Apply());

        Assert.Equal("new exe", _root.Read(@"app\Dongled.exe"));
        Assert.Equal("new dll", _root.Read(@"app\Dongled.dll"));
        Assert.Equal("new file", _root.Read(@"app\Added.dll"));
        Assert.Equal("new icon", _root.Read(@"app\Assets\TrayIcon.ico"));
        Assert.Equal("new plugin", _root.Read(@"app\Plugins\HyperXHid\Dongled.Plugin.HyperXHid.dll"));
        Assert.Equal("newly bundled plugin", _root.Read(@"app\Plugins\Logitech\Dongled.Plugin.Logitech.dll"));
    }

    [Fact]
    public void A_file_outside_the_plugins_that_the_new_version_does_not_ship_is_left_alone()
    {
        Apply();

        Assert.Equal("not shipped any more", _root.Read(@"app\Kept.dll"));
    }

    [Fact]
    public void A_bundled_plugin_folder_ends_up_exactly_as_shipped()
    {
        Apply();

        Assert.Equal(
            ["Dongled.Plugin.HyperXHid.dll"],
            Directory.EnumerateFiles(_root.Combine(@"app\Plugins\HyperXHid")).Select(Path.GetFileName));
    }

    [Fact]
    public void A_plugin_folder_the_user_added_is_not_touched()
    {
        var before = File.GetLastWriteTimeUtc(_root.Combine(@"app\Plugins\MyOwnPlugin\Dongled.Plugin.Mine.dll"));

        Apply();

        Assert.Equal("the user's plugin", _root.Read(@"app\Plugins\MyOwnPlugin\Dongled.Plugin.Mine.dll"));
        Assert.Equal(before, File.GetLastWriteTimeUtc(_root.Combine(@"app\Plugins\MyOwnPlugin\Dongled.Plugin.Mine.dll")));
        Assert.DoesNotContain(BackedUp(), path => path.Contains("MyOwnPlugin", StringComparison.Ordinal));
    }

    [Fact]
    public void Replaced_files_are_moved_aside_outside_the_plugins_folder_and_cleaned_up_later()
    {
        Apply();

        Assert.Equal(5, BackedUp().Length);
        Assert.Empty(Directory.EnumerateFiles(_root.Combine(@"app\Plugins"), "*.old", SearchOption.AllDirectories));

        Assert.True(AppFolderSwap.CleanUp(App, NullLogger.Instance));
        Assert.False(Directory.Exists(_root.Combine(@"app", AppFolderSwap.BackupDirectoryName)));
    }

    [Fact]
    public void Cleaning_up_with_nothing_to_clean_is_harmless() =>
        Assert.True(AppFolderSwap.CleanUp(App, NullLogger.Instance));

    [Fact]
    public void A_file_still_in_use_is_left_for_the_next_cleanup()
    {
        Apply();
        var aside = BackedUp()[0];

        using (new FileStream(aside, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(AppFolderSwap.CleanUp(App, NullLogger.Instance));
        }

        Assert.True(AppFolderSwap.CleanUp(App, NullLogger.Instance));
    }

    [Fact]
    public void A_failure_part_way_through_puts_every_original_back()
    {
        // Sorted order puts the plugins after everything at the root, so by the time this file is
        // reached the executable and the root DLL have already been swapped.
        var locked = _root.Combine(@"app\Plugins\HyperXHid\Retired.dll");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<UpdateException>(() => Apply());
            Assert.Contains("nothing was changed", ex.Message, StringComparison.Ordinal);
        }

        Assert.Equal("old exe", _root.Read(@"app\Dongled.exe"));
        Assert.Equal("old dll", _root.Read(@"app\Dongled.dll"));
        Assert.Equal("old icon", _root.Read(@"app\Assets\TrayIcon.ico"));
        Assert.Equal("old plugin", _root.Read(@"app\Plugins\HyperXHid\Dongled.Plugin.HyperXHid.dll"));
        Assert.Equal("a file the new plugin no longer ships", _root.Read(@"app\Plugins\HyperXHid\Retired.dll"));
        Assert.False(File.Exists(_root.Combine(@"app\Added.dll")));
        Assert.False(Directory.Exists(_root.Combine(@"app\Plugins\Logitech")));
        Assert.Empty(BackedUp());

        // The new files went back where they came from, so the attempt can be repeated.
        Assert.Equal("new exe", _root.Read(@"new\Dongled.exe"));
    }

    [Fact]
    public void A_file_that_collides_with_a_folder_rolls_everything_back()
    {
        _root.Write(@"new\Zzz", "a file where the app has a folder");
        Directory.CreateDirectory(_root.Combine(@"app\Zzz"));

        Assert.Throws<UpdateException>(() => Apply());

        Assert.Equal("old exe", _root.Read(@"app\Dongled.exe"));
        Assert.Equal("old plugin", _root.Read(@"app\Plugins\HyperXHid\Dongled.Plugin.HyperXHid.dll"));
    }

    [Fact]
    public void A_writable_folder_passes_the_check() =>
        AppFolderSwap.EnsureWritable(App);

    [Fact]
    public void A_missing_folder_fails_the_check_with_a_sentence()
    {
        var ex = Assert.Throws<UpdateException>(() => AppFolderSwap.EnsureWritable(_root.Combine("nowhere")));

        Assert.Contains("cannot write to the folder", ex.Message, StringComparison.Ordinal);
    }
}
