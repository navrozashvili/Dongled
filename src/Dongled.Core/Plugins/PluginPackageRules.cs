using System.Globalization;
using System.IO;
using System.Text;

namespace Dongled.Core.Plugins;

/// <summary>
/// Turns an archive's file name into a plugin directory name.
/// </summary>
internal static class PluginPackageRules
{
    /// <summary>The longest folder name accepted, well inside any path limit once combined.</summary>
    private const int MaximumNameLength = 64;

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>Turn an archive's file name into a directory name that Windows and the configuration file can both hold.</summary>
    /// <param name="candidate">The archive's file name, extension included.</param>
    /// <param name="name">The directory name, when this returns <see langword="true"/>.</param>
    /// <param name="failure">A sentence naming what is wrong. Empty on success.</param>
    /// <remarks>
    /// Stricter than the file system needs, because this value is not only a directory name: it is
    /// the key every trust record in <c>config.json</c> is written under, and the identifier the
    /// Plugins page shows.
    /// </remarks>
    public static bool TrySanitiseDirectoryName(string candidate, out string name, out string failure)
    {
        name = string.Empty;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            failure = "This file has no name to take a folder name from.";
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(candidate.Trim());
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(stem.Length);

        foreach (var character in stem)
        {
            if (Array.IndexOf(invalid, character) < 0)
            {
                builder.Append(character);
            }
        }

        // A trailing dot or space is accepted by some APIs and silently dropped by others, which
        // would leave the configuration keyed on a name that no longer matches the folder.
        var cleaned = builder.ToString().Trim().TrimEnd('.', ' ');

        if (cleaned.Length == 0)
        {
            failure = "There is no usable folder name in this file's name. Rename the file and add it again.";
            return false;
        }

        if (cleaned.Length > MaximumNameLength)
        {
            failure = string.Create(
                CultureInfo.InvariantCulture,
                $"This file's name is longer than {MaximumNameLength} characters. Rename it and add it again.");
            return false;
        }

        if (Array.Exists(ReservedNames, reserved => string.Equals(reserved, cleaned, StringComparison.OrdinalIgnoreCase)))
        {
            failure = $"'{cleaned}' is a name Windows reserves for a device. Rename the file and add it again.";
            return false;
        }

        name = cleaned;
        failure = string.Empty;
        return true;
    }
}
