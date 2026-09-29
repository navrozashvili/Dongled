using System.IO;
using Dongled.Core.Configuration;
using Xunit;

namespace Dongled.Core.Tests.Configuration;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ass-tests-" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory must never fail a test run.
        }
    }

    private string PathFor(string name) => Path.Combine(_directory, name);

    [Fact]
    public void Writing_then_reading_round_trips_the_content()
    {
        var path = PathFor("round-trip.json");

        AtomicFile.WriteAllText(path, "{\"hello\":\"world\"}");

        Assert.Equal("{\"hello\":\"world\"}", AtomicFile.ReadAllTextOrNull(path));
    }

    [Fact]
    public void Reading_a_file_that_does_not_exist_returns_null_rather_than_throwing()
    {
        Assert.Null(AtomicFile.ReadAllTextOrNull(PathFor("absent.json")));
    }

    [Fact]
    public void Writing_creates_missing_parent_directories()
    {
        var path = Path.Combine(_directory, "nested", "deeper", "config.json");

        AtomicFile.WriteAllText(path, "{}");

        Assert.Equal("{}", AtomicFile.ReadAllTextOrNull(path));
    }

    [Fact]
    public void Overwriting_replaces_the_previous_content_entirely()
    {
        // The failure this guards against: a shorter payload leaving a tail of the longer
        // previous one behind, which is what a plain truncating write can produce if it fails
        // partway. The replacement is a rename, so the file is never partially written.
        var path = PathFor("shrink.json");
        AtomicFile.WriteAllText(path, new string('x', 4096));

        AtomicFile.WriteAllText(path, "{}");

        Assert.Equal("{}", AtomicFile.ReadAllTextOrNull(path));
    }

    [Fact]
    public void A_failed_write_leaves_the_previous_content_intact()
    {
        // Simulates a mid-write failure: the destination is locked by another handle, so the
        // replace cannot complete.
        var path = PathFor("locked.json");
        AtomicFile.WriteAllText(path, "{\"original\":true}");

        using (var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => AtomicFile.WriteAllText(path, "{\"replacement\":true}"));
        }

        Assert.Equal("{\"original\":true}", AtomicFile.ReadAllTextOrNull(path));
    }

    [Fact]
    public void A_failed_write_does_not_leave_temporary_files_behind()
    {
        var path = PathFor("no-litter.json");
        AtomicFile.WriteAllText(path, "{\"original\":true}");

        using (var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => AtomicFile.WriteAllText(path, "{\"replacement\":true}"));
        }

        Assert.Equal(new[] { "no-litter.json" }, Directory.GetFiles(_directory).Select(Path.GetFileName).ToArray());
    }
}
