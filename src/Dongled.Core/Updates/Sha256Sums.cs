namespace Dongled.Core.Updates;

/// <summary>Reads a <c>SHA256SUMS</c> file in the format <c>sha256sum</c> writes.</summary>
public static class Sha256Sums
{
    /// <summary>The asset name the release pipeline publishes the checksums under.</summary>
    public const string FileName = "SHA256SUMS";

    /// <summary>
    /// Find the checksum listed for <paramref name="fileName"/>.
    /// </summary>
    /// <returns>The checksum as lowercase hex, or null if the file does not list that name exactly once with a well-formed hash.</returns>
    /// <remarks>
    /// Each line is <c>&lt;64 hex digits&gt;</c>, white space, then the name, optionally prefixed
    /// with <c>*</c> for binary mode. A name listed twice is treated as not listed: two different
    /// answers to the same question are not something to pick between.
    /// </remarks>
    public static string? Find(string contents, string fileName)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        string? found = null;

        foreach (var rawLine in contents.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length < 66)
            {
                continue;
            }

            var hash = line[..64];
            if (!hash.All(char.IsAsciiHexDigit) || !char.IsWhiteSpace(line[64]))
            {
                continue;
            }

            var name = line[64..].TrimStart();
            if (name.StartsWith('*'))
            {
                name = name[1..];
            }

            if (!string.Equals(name, fileName, StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                return null;
            }

            found = hash.ToLowerInvariant();
        }

        return found;
    }
}
