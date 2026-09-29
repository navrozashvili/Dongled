using System.Text.Json;

namespace Dongled.Core.Updates;

/// <summary>One file attached to a release.</summary>
/// <param name="Name">The file name, exactly as uploaded.</param>
/// <param name="DownloadUrl">Where to fetch it.</param>
/// <param name="Size">Its size in bytes as GitHub reports it, or zero if not reported.</param>
public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size);

/// <summary>A published release, reduced to what updating needs.</summary>
/// <param name="Version">The numeric version from the tag.</param>
/// <param name="VersionText">The tag without its leading <c>v</c>, which is how asset names spell the version.</param>
/// <param name="Assets">The attached files that come from this repository's release downloads.</param>
public sealed record ReleaseInfo(Version Version, string VersionText, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>The release's page on GitHub, which holds its notes.</summary>
    public Uri ReleaseNotesUrl => UpdateEndpoints.ReleasePage(VersionText);

    /// <summary>Find an asset by exact name.</summary>
    public ReleaseAsset? FindAsset(string name) =>
        Assets.FirstOrDefault(asset => string.Equals(asset.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Read the body of <c>GET /repos/{owner}/{repo}/releases/latest</c>.
    /// </summary>
    /// <returns>The release, or null if the document is not a usable, published, full release.</returns>
    /// <remarks>
    /// <para>
    /// A draft or a pre-release is refused even though <c>/releases/latest</c> should never return
    /// one, so a change on GitHub's side cannot make the app offer something that was not meant to
    /// be installed.
    /// </para>
    /// <para>
    /// An asset whose download address does not point at this repository's release downloads is
    /// left out, as if it were missing. The response is not signed, and nothing it says should be
    /// able to send the app somewhere else for the file it is about to run.
    /// </para>
    /// </remarks>
    public static ReleaseInfo? Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || IsTrue(root, "draft")
                || IsTrue(root, "prerelease")
                || !root.TryGetProperty("tag_name", out var tagElement)
                || tagElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var tag = tagElement.GetString() ?? string.Empty;
            if (!tag.StartsWith('v') || !ReleaseVersion.TryParse(tag, out var version))
            {
                return null;
            }

            var versionText = tag[1..];

            // Used verbatim in asset names, the release page address and a staging path, so
            // anything beyond digits and dots is refused rather than escaped.
            if (versionText.Length == 0 || versionText.Any(c => c != '.' && !char.IsAsciiDigit(c)))
            {
                return null;
            }

            var assets = new List<ReleaseAsset>();

            if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElement.EnumerateArray())
                {
                    if (TryReadAsset(asset, tag, out var parsed))
                    {
                        assets.Add(parsed);
                    }
                }
            }

            return new ReleaseInfo(version, versionText, assets);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsTrue(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool TryReadAsset(JsonElement asset, string tag, out ReleaseAsset parsed)
    {
        parsed = null!;

        if (asset.ValueKind != JsonValueKind.Object
            || !asset.TryGetProperty("name", out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String
            || !asset.TryGetProperty("browser_download_url", out var urlElement)
            || urlElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var name = nameElement.GetString();
        var url = urlElement.GetString();

        if (string.IsNullOrEmpty(name)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !UpdateEndpoints.IsReleaseDownload(uri, tag))
        {
            return false;
        }

        var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var bytes)
            ? bytes
            : 0;

        parsed = new ReleaseAsset(name, uri, size);
        return true;
    }
}
