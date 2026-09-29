using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Configuration;

/// <summary>
/// Keeps a store from destroying a file written by a newer build than the one running.
/// </summary>
/// <remarks>
/// <para>
/// Every store here promises the same thing in its schema version doc comment: a file declaring a
/// version this build does not understand is treated as unreadable, defaults are used, and the
/// file is left on disk so running an older build cannot silently destroy a newer configuration.
/// Refusing to read such a file only honours the first half. The store still saves later, and
/// that save is what overwrites the file the user was never shown.
/// </para>
/// <para>
/// The logic lives here rather than in each store so they all call the same code and cannot
/// drift apart.
/// </para>
/// </remarks>
internal static class SchemaGuard
{
    private const string SchemaVersionProperty = "schemaVersion";

    /// <summary>
    /// Copy the file at <paramref name="filePath"/> aside if it declares a schema version higher
    /// than <paramref name="supportedSchemaVersion"/>, so the write about to follow does not
    /// destroy it. The backup goes to <c>&lt;path&gt;.newer-v&lt;N&gt;.bak</c>.
    /// </summary>
    /// <param name="filePath">Full path to the file that is about to be overwritten.</param>
    /// <param name="supportedSchemaVersion">Highest schema version the calling store understands.</param>
    /// <param name="logger">Where to report the rescue, or a failure to attempt it.</param>
    /// <remarks>
    /// <para>
    /// Never throws. If the file is absent, unreadable, not JSON, or carries no
    /// <c>schemaVersion</c>, there is nothing to preserve and the caller proceeds with its write.
    /// A genuine write failure still reaches the caller, because it comes from the caller's own
    /// <see cref="AtomicFile.WriteAllText"/> call rather than from here.
    /// </para>
    /// <para>
    /// The check reads the file rather than trusting what the store's own load saw, so it holds
    /// whether or not the store loaded first, and whether or not another process replaced the file
    /// in between.
    /// </para>
    /// </remarks>
    public static void PreserveNewerFile(string filePath, int supportedSchemaVersion, ILogger logger)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            // Only the version is read. A newer file may not fit this build's shape at all, so
            // deserializing the whole document could fail on a file that is perfectly valid.
            var options = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };

            using var document = JsonDocument.Parse(File.ReadAllText(filePath), options);
            if (document.RootElement.ValueKind is not JsonValueKind.Object
                || !TryReadSchemaVersion(document.RootElement, out var fileVersion)
                || fileVersion <= supportedSchemaVersion)
            {
                return;
            }

            var backupPath = $"{filePath}.newer-v{fileVersion}.bak";

            // Overwrite: a second downgrade must not fail the save, and the file being replaced
            // is the same newer document the first backup already holds.
            File.Copy(filePath, backupPath, overwrite: true);

            logger.LogWarning(
                "The file at {Path} declares schema version {FileVersion}, which this build does not understand, and is about to be overwritten. A copy was saved to {BackupPath}.",
                filePath,
                fileVersion,
                backupPath);
        }
        catch (Exception ex)
        {
            // Deliberately broad and deliberately swallowed. This is a best-effort rescue of a
            // file that is about to be replaced: if it cannot be read, parsed, or copied there is
            // nothing here worth preserving, and refusing to save because the rescue failed would
            // turn a lost backup into a lost setting.
            logger.LogWarning(
                ex,
                "Could not check the existing file at {Path} for a newer schema version. Continuing with the write.",
                filePath);
        }
    }

    /// <summary>
    /// Read the file's declared schema version, matching the property name without regard to case.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The casing has to be ignored because the stores deserialize with
    /// <see cref="JsonSerializerOptions.PropertyNameCaseInsensitive"/> set, so a file writing
    /// <c>SchemaVersion</c> or <c>SCHEMAVERSION</c> is read as a newer file by the load path and
    /// left alone there. A case-sensitive lookup here would then see no version at all and let the
    /// next save destroy it: exactly the file the guard exists for, and silently unguarded.
    /// </para>
    /// </remarks>
    private static bool TryReadSchemaVersion(JsonElement root, out int version)
    {
        var found = false;
        version = 0;

        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, SchemaVersionProperty, StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind is not JsonValueKind.Number
                || !property.Value.TryGetInt32(out var candidate))
            {
                continue;
            }

            // JSON allows the same name twice, and case insensitivity means two spellings count as
            // the same name. Take the highest rather than the first or last: this is a rescue, and
            // a backup that turns out not to have been needed costs one stray file, where a missed
            // one costs the user the configuration it was meant to preserve.
            version = found ? Math.Max(version, candidate) : candidate;
            found = true;
        }

        return found;
    }
}
