using System.IO;
using System.Text;

namespace Dongled.Core.Configuration;

/// <summary>
/// Writes a file so a reader sees either the whole previous content or the whole new content,
/// never a partial write.
/// </summary>
/// <remarks>
/// <para>
/// Each file has one writer, but the guarantee does not depend on that: the rename is atomic
/// either way, so two concurrent writers produce last-writer-wins with no corruption, and a reader
/// still sees one whole version or the other. The only concurrency hazard is a narrow window in
/// which <see cref="File.Exists(string)"/> and the rename that follows it disagree about whether
/// the destination is present, which produces a spurious throw rather than a bad file. The app's
/// single-instance guard is therefore a nicety here, not a correctness prerequisite.
/// </para>
/// <para>
/// Two limits qualify the claim that a failed write leaves the previous state intact. Parent
/// directories are created before the write is attempted and are not unwound, so a failed write
/// into a previously nonexistent path leaves the empty directories behind; that is deliberate,
/// because unwinding directory creation is racy and empty directories are harmless. And a crash
/// mid-write leaves the temporary file behind, because the cleanup cannot run when the process
/// dies, which is precisely the case this helper exists for. Those leftovers are harmless: the
/// name never matches a <c>*.json</c> enumeration and nothing reads them.
/// </para>
/// </remarks>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Write <paramref name="contents"/> to <paramref name="path"/>, creating parent directories
    /// as needed. Writes to a temporary file in the same directory, flushes it to disk, then
    /// renames it over the destination.
    /// </summary>
    /// <exception cref="IOException">
    /// The write or the replacement failed. The destination still holds its previous content.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The caller lacks permission to replace the destination, because it is read-only or because
    /// an ACL denies the write. As with <see cref="IOException"/>, the destination still holds its
    /// previous content. This type does not derive from <see cref="IOException"/>, so a caller that
    /// catches only that one will not catch this.
    /// </exception>
    public static void WriteAllText(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Same directory as the destination, so the rename stays on one volume and is atomic.
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            // Flush to disk before the rename, so a power loss cannot leave the destination
            // pointing at a file whose contents never reached the platter.
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                // Replace rather than Move: it is atomic and preserves the destination's
                // attributes. No backup file is requested, so nothing extra is left behind.
                File.Replace(temporaryPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        catch
        {
            DeleteIfPossible(temporaryPath);
            throw;
        }
    }

    /// <summary>
    /// Read a file, returning null if it does not exist. Any other failure propagates, because a
    /// file that exists but cannot be read is a real problem the caller must decide about.
    /// </summary>
    public static string? ReadAllTextOrNull(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            return File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void DeleteIfPossible(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Cleanup is best effort; the original failure is what matters. The catch is broad
            // because File.Delete can also reject a malformed path with NotSupportedException or
            // ArgumentException, and a cleanup failure must never replace the failure the caller
            // needs to see.
        }
    }
}
