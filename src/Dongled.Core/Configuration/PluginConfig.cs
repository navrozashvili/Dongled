using Microsoft.Extensions.Logging;

namespace Dongled.Core.Configuration;

/// <summary>
/// Trust and logging settings for one plugin directory.
/// </summary>
/// <remarks>
/// The trust unit is a directory, not a single assembly, because pinning only the main file
/// would leave its dependencies beside it unpinned and equally executable. An entry exists
/// only once a user has approved the plugin; absence means blocked.
/// </remarks>
public sealed class PluginConfig
{
    /// <summary>Directory name under the plugins folder, for example <c>HyperXHid</c>.</summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>
    /// Whether the user has enabled this plugin. Defaults to false so an unapproved plugin is
    /// never loaded.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// SHA-256 over the sorted list of relative path and file hash pairs for the directory.
    /// Any file added, changed, or removed changes this and re-blocks the plugin.
    /// </summary>
    public string? ManifestSha256 { get; set; }

    /// <summary>When the user approved the recorded hash.</summary>
    public DateTimeOffset? ApprovedUtc { get; set; }

    /// <summary>
    /// Minimum severity recorded for this plugin. Warning in every build configuration, so a
    /// bug report from a release user shows the same lines a developer sees.
    /// </summary>
    public LogLevel LogLevel { get; set; } = LogLevel.Warning;
}
