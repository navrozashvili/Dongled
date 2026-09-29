using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Dongled.Core.Plugins;

/// <summary>One file inside a plugin directory, and the hash that pins it.</summary>
/// <param name="RelativePath">
/// Path relative to the plugin directory, with <c>/</c> separators so the value is stable and
/// readable wherever it is displayed.
/// </param>
/// <param name="Sha256">Uppercase hexadecimal SHA-256 of the file's contents.</param>
public sealed record PluginFileHash(string RelativePath, string Sha256);

/// <summary>
/// The integrity record for one plugin directory: a single hash covering every file it contains,
/// plus the per-file hashes needed to say which files differ.
/// </summary>
/// <remarks>
/// <para>
/// The trust unit is the directory, not the main assembly. Pinning only
/// <c>Dongled.Plugin.HyperXHid.dll</c> would leave <c>HidSharp.dll</c> beside it
/// unpinned and equally executable: a planted or swapped dependency would run under an approval
/// that never covered it.
/// </para>
/// <para>
/// Every file counts, including <c>.pdb</c> and <c>.deps.json</c>. Rebuilding a plugin therefore
/// re-blocks it until the user approves it again. That is the intended trade: a manifest that
/// ignored some files would be a manifest with a documented place to hide something.
/// </para>
/// <para>
/// Computation fails rather than degrades. An unreadable file, a link, or a missing directory
/// throws, because a manifest that silently omitted an entry would hash the same as an honest one.
/// </para>
/// </remarks>
public sealed class PluginManifest
{
    /// <summary>
    /// How deep the walk goes before refusing. No real plugin layout approaches this; a directory
    /// tree built to exhaust the stack does.
    /// </summary>
    private const int MaximumDepth = 32;

    private static readonly EnumerationOptions Enumeration = new()
    {
        // Every default here is wrong for this purpose. AttributesToSkip defaults to
        // Hidden | System, which would leave a hidden dependency out of the manifest while
        // AssemblyDependencyResolver would still load it. IgnoreInaccessible defaults to true,
        // which would turn "this file cannot be read" into "this file does not exist".
        AttributesToSkip = FileAttributes.None,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private static readonly byte[] FieldSeparator = [0x00];
    private static readonly byte[] RecordSeparator = [0x0A];

    private PluginManifest(string sha256, IReadOnlyList<PluginFileHash> files)
    {
        Sha256 = sha256;
        Files = files;
    }

    /// <summary>
    /// Uppercase hexadecimal SHA-256 over the sorted list of relative path and file hash pairs.
    /// This is the value approval records.
    /// </summary>
    public string Sha256 { get; }

    /// <summary>Every file in the directory, ordinal-sorted by relative path.</summary>
    public IReadOnlyList<PluginFileHash> Files { get; }

    /// <summary>Hash a plugin directory and everything under it.</summary>
    /// <param name="directory">The plugin's own folder.</param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is blank.</exception>
    /// <exception cref="DirectoryNotFoundException">There is no such directory.</exception>
    /// <exception cref="IOException">
    /// The directory or something inside it is a link, is nested deeper than the depth limit, or
    /// could not be read.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">Something inside it could not be read.</exception>
    public static PluginManifest Compute(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var root = new DirectoryInfo(directory);
        if (!root.Exists)
        {
            throw new DirectoryNotFoundException($"There is no plugin directory at '{directory}'.");
        }

        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                $"'{root.Name}' is a link. A plugin directory is hashed as a unit, so it must be the "
                + "real directory: a link points at content that can be changed without changing "
                + "anything here.");
        }

        var files = new List<PluginFileHash>();
        Walk(root, prefix: string.Empty, depth: 0, files);
        files.Sort(static (left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            // A NUL between the two fields is unambiguous because no Windows path may contain one,
            // so no pair of path and hash values can be made to serialize identically to another.
            hash.AppendData(Encoding.UTF8.GetBytes(file.RelativePath));
            hash.AppendData(FieldSeparator);
            hash.AppendData(Encoding.UTF8.GetBytes(file.Sha256));
            hash.AppendData(RecordSeparator);
        }

        return new PluginManifest(Convert.ToHexString(hash.GetHashAndReset()), files);
    }

    /// <summary>Whether a hash recorded at approval still describes this directory.</summary>
    /// <param name="recordedSha256">
    /// The value from <see cref="Configuration.PluginConfig.ManifestSha256"/>. A blank or missing
    /// value never matches, which is what makes an approval record with no hash useless rather
    /// than permissive.
    /// </param>
    public bool Matches(string? recordedSha256) =>
        !string.IsNullOrWhiteSpace(recordedSha256)
        && string.Equals(Sha256, recordedSha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Which files differ between this manifest and another: added, removed, or changed, named by
    /// relative path and ordinal-sorted. This is what lets the UI say what changed instead of only
    /// that something did.
    /// </summary>
    /// <param name="other">Usually a manifest taken at approval time.</param>
    public IReadOnlyList<string> DifferingFiles(PluginManifest other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var mine = Files.ToDictionary(file => file.RelativePath, file => file.Sha256, StringComparer.Ordinal);
        var theirs = other.Files.ToDictionary(file => file.RelativePath, file => file.Sha256, StringComparer.Ordinal);

        var differing = new List<string>();

        foreach (var (path, sha) in mine)
        {
            if (!theirs.TryGetValue(path, out var previous)
                || !string.Equals(sha, previous, StringComparison.Ordinal))
            {
                differing.Add(path);
            }
        }

        foreach (var path in theirs.Keys)
        {
            if (!mine.ContainsKey(path))
            {
                differing.Add(path);
            }
        }

        differing.Sort(StringComparer.Ordinal);
        return differing;
    }

    private static void Walk(DirectoryInfo directory, string prefix, int depth, List<PluginFileHash> files)
    {
        if (depth > MaximumDepth)
        {
            throw new IOException(
                $"'{prefix}' is nested more than {MaximumDepth} directories deep, which no real "
                + "plugin layout needs.");
        }

        foreach (var entry in directory.EnumerateFileSystemInfos("*", Enumeration))
        {
            var relative = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;

            // Reading Attributes on an entry that has vanished or cannot be reached throws, which
            // is the outcome this method wants anyway.
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    $"'{relative}' is a link. Everything a plugin directory pins has to live inside "
                    + "it, or the hash would cover a name rather than the bytes that get loaded.");
            }

            if (entry is DirectoryInfo subdirectory)
            {
                Walk(subdirectory, relative, depth + 1, files);
            }
            else
            {
                files.Add(new PluginFileHash(relative, HashFile(entry.FullName)));
            }
        }
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
