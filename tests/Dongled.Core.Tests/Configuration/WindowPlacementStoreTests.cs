using System.IO;
using Dongled.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dongled.Core.Tests.Configuration;

public sealed class WindowPlacementStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ass-window-" + Guid.NewGuid().ToString("N"));

    public WindowPlacementStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string PlacementPath => Path.Combine(_directory, "window.json");

    private WindowPlacementStore CreateStore() =>
        new(PlacementPath, NullLogger<WindowPlacementStore>.Instance);

    [Fact]
    public void Nothing_is_remembered_before_anything_is_saved()
    {
        Assert.Null(CreateStore().Load());
    }

    [Fact]
    public void A_placement_survives_a_reload()
    {
        CreateStore().Save(new WindowPlacement { X = 120, Y = 80, Width = 1400, Height = 900, Maximized = true });

        var reloaded = CreateStore().Load();

        Assert.NotNull(reloaded);
        Assert.Equal(120, reloaded.X);
        Assert.Equal(80, reloaded.Y);
        Assert.Equal(1400, reloaded.Width);
        Assert.Equal(900, reloaded.Height);
        Assert.True(reloaded.Maximized);
    }

    [Fact]
    public void A_negative_position_survives_a_reload()
    {
        // A monitor to the left of the primary one has negative coordinates, and a window on it is
        // a perfectly ordinary thing to want back.
        CreateStore().Save(new WindowPlacement { X = -1800, Y = -200, Width = 1200, Height = 800 });

        var reloaded = CreateStore().Load();

        Assert.NotNull(reloaded);
        Assert.Equal(-1800, reloaded.X);
        Assert.Equal(-200, reloaded.Y);
    }

    [Fact]
    public void A_malformed_file_reads_as_nothing_remembered()
    {
        File.WriteAllText(PlacementPath, "{ this is not json");

        Assert.Null(CreateStore().Load());
    }

    [Fact]
    public void A_newer_file_is_not_read()
    {
        File.WriteAllText(
            PlacementPath,
            """{ "schemaVersion": 99, "x": 10, "y": 10, "width": 100, "height": 100 }""");

        Assert.Null(CreateStore().Load());
    }

    [Fact]
    public void A_newer_file_is_copied_aside_before_it_is_replaced()
    {
        // The same promise the other two stores make. A downgrade must not silently destroy a file
        // it could not read.
        File.WriteAllText(
            PlacementPath,
            """{ "schemaVersion": 99, "x": 10, "y": 10, "width": 100, "height": 100 }""");

        CreateStore().Save(new WindowPlacement { X = 0, Y = 0, Width = 1200, Height = 800 });

        Assert.True(File.Exists(PlacementPath + ".newer-v99.bak"));
    }

    [Fact]
    public void A_write_that_cannot_succeed_does_not_throw()
    {
        // The one place this store parts company with the other two. It runs while the window is
        // being put away or the process is ending, so there is nothing to report to and nothing
        // that behaves differently; the next start just uses the default position. A directory
        // where the file should be is the simplest way to make the write fail for certain.
        Directory.CreateDirectory(PlacementPath);

        CreateStore().Save(new WindowPlacement { X = 0, Y = 0, Width = 1200, Height = 800 });
    }

    [Fact]
    public void A_read_that_cannot_succeed_does_not_throw()
    {
        Directory.CreateDirectory(PlacementPath);

        Assert.Null(CreateStore().Load());
    }
}
