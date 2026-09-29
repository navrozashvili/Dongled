namespace Dongled.Core.Updates;

/// <summary>The only addresses the updater ever talks to.</summary>
public static class UpdateEndpoints
{
    /// <summary>The repository releases are published from.</summary>
    public const string Repository = "navrozashvili/Dongled";

    /// <summary>The one API call a check makes. Unauthenticated, so GitHub allows sixty an hour per address.</summary>
    public static Uri LatestRelease { get; } = new($"https://api.github.com/repos/{Repository}/releases/latest");

    /// <summary>The page listing every release.</summary>
    public static Uri ReleasesPage { get; } = new($"https://github.com/{Repository}/releases");

    /// <summary>A release's own page, which holds its notes.</summary>
    /// <param name="versionText">The version as the tag spells it, without the leading <c>v</c>.</param>
    public static Uri ReleasePage(string versionText) =>
        new($"https://github.com/{Repository}/releases/tag/v{Uri.EscapeDataString(versionText)}");

    /// <summary>
    /// Whether <paramref name="uri"/> is a download from this repository's release
    /// <paramref name="tag"/>. GitHub then redirects to its storage host, which the HTTP client
    /// follows over HTTPS.
    /// </summary>
    public static bool IsReleaseDownload(Uri uri, string tag)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var prefix = $"/{Repository}/releases/download/{tag}/";

        return uri.IsAbsoluteUri
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
