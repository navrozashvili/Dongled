using System.IO;
using System.Text.Json;

namespace Dongled.Core.Plugins;

/// <summary>A plugin directory shipped with this build of Dongled, pinned by its manifest hash.</summary>
/// <param name="Directory">The directory name under the plugins root, for example <c>HyperXHid</c>.</param>
/// <param name="Sha256">The <see cref="PluginManifest.Sha256"/> the release computed for it.</param>
public sealed record BundledPlugin(string Directory, string Sha256);

/// <summary>
/// The plugins a release shipped, compiled into this assembly, and the one question the loader asks
/// of them: are these exactly the files that were shipped?
/// </summary>
/// <remarks>
/// <para>
/// A directory whose name and whole-folder manifest hash both match an entry is trusted the same
/// way the host's own binaries are, because the list is part of those binaries: whoever can change
/// it can change the loader too. Anything else, including a shipped plugin with a single file
/// added, changed or removed, gets no benefit from this and goes through approval as usual.
/// </para>
/// <para>
/// Only a release build embeds a list. Every other build embeds nothing, so
/// <see cref="Embedded"/> is empty and every plugin needs the user's approval.
/// </para>
/// </remarks>
public sealed class BundledPluginTrust
{
    /// <summary>The manifest resource the release build embeds. Absent in every other build.</summary>
    internal const string ResourceName = "Dongled.Core.Plugins.BundledPlugins.json";

    private static readonly Lazy<BundledPluginTrust> EmbeddedList = new(LoadEmbedded);

    /// <param name="entries">The shipped plugins.</param>
    public BundledPluginTrust(IEnumerable<BundledPlugin> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Entries = [.. entries];
    }

    /// <summary>Trusts nothing. What every build but a release has.</summary>
    public static BundledPluginTrust None { get; } = new([]);

    /// <summary>
    /// The list compiled into this assembly. Empty when there is none, and also when the one there
    /// cannot be read: failing to read it must never widen trust.
    /// </summary>
    public static BundledPluginTrust Embedded => EmbeddedList.Value;

    /// <summary>The shipped plugins.</summary>
    public IReadOnlyList<BundledPlugin> Entries { get; }

    /// <summary>
    /// Whether a directory is a shipped plugin, unchanged: its name matches an entry and its whole
    /// manifest hash matches that entry's.
    /// </summary>
    /// <param name="directory">The directory name under the plugins root.</param>
    /// <param name="sha256">Its manifest hash right now. A blank value never matches.</param>
    public bool Covers(string directory, string? sha256)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (string.IsNullOrWhiteSpace(sha256))
        {
            return false;
        }

        // The name comparison ignores case for the same reason the configuration lookup does: the
        // file system that produced the name does not distinguish them. The hash comparison ignores
        // case because it is hexadecimal, and is the same test PluginManifest.Matches makes.
        return Entries.Any(entry =>
            string.Equals(entry.Directory, directory, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(entry.Sha256)
            && string.Equals(entry.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Read the list the release tool writes.</summary>
    /// <param name="json">
    /// A document with a <c>plugins</c> array whose items have at least <c>name</c> and
    /// <c>sha256</c>. Other properties are ignored.
    /// </param>
    /// <exception cref="JsonException">The document is not that shape.</exception>
    internal static BundledPluginTrust Parse(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("plugins", out var plugins)
            || plugins.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The bundled plugin list has no 'plugins' array.");
        }

        var entries = new List<BundledPlugin>();
        foreach (var plugin in plugins.EnumerateArray())
        {
            var name = RequiredString(plugin, "name");
            var sha256 = RequiredString(plugin, "sha256");
            entries.Add(new BundledPlugin(name, sha256));
        }

        return new BundledPluginTrust(entries);
    }

    private static string RequiredString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new JsonException($"An entry in the bundled plugin list has no '{property}'.");
        }

        return value.GetString()!;
    }

    private static BundledPluginTrust LoadEmbedded()
    {
        try
        {
            using var stream = typeof(BundledPluginTrust).Assembly.GetManifestResourceStream(ResourceName);
            return stream is null ? None : Parse(stream);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return None;
        }
    }
}
