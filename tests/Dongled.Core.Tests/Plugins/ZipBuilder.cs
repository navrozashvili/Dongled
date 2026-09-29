using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Dongled.Core.Tests.Plugins;

/// <summary>Builds the archives the installer tests feed in.</summary>
/// <remarks>
/// Entries are written by name rather than by zipping a directory, so a test can produce shapes the
/// file system would refuse to hold — a traversal path, two names differing only by case — which is
/// exactly the input the reader has to survive.
/// </remarks>
internal static class ZipBuilder
{
    /// <summary>Write an archive whose entries are exactly the given names and bytes.</summary>
    public static string FromFiles(string destinationZipPath, IReadOnlyDictionary<string, byte[]> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationZipPath)!);

        using var stream = new FileStream(destinationZipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = entry.Open();
            writer.Write(content);
        }

        return destinationZipPath;
    }

    /// <summary>
    /// Write an archive holding a real directory's files, optionally nested inside one folder so the
    /// wrapper-stripping path can be exercised against genuine plugin output.
    /// </summary>
    public static string FromDirectory(string destinationZipPath, string sourceDirectory, string? wrapperFolder = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationZipPath)!);

        using var stream = new FileStream(destinationZipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
            var name = wrapperFolder is null ? relative : wrapperFolder + "/" + relative;
            archive.CreateEntryFromFile(file, name);
        }

        return destinationZipPath;
    }
}
