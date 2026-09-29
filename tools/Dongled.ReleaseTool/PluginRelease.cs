using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dongled.Core.Plugins;

namespace Dongled.ReleaseTool;

/// <summary>One plugin folder as a release ships it.</summary>
/// <param name="Name">The folder name under <c>Plugins\</c>, which is also its configuration key.</param>
/// <param name="Version">The plugin assembly's informational version, from its csproj.</param>
/// <param name="Sha256">The folder's <see cref="PluginManifest.Sha256"/>.</param>
/// <param name="Files">Every file's relative path and hash, so a failure can say which files changed.</param>
internal sealed record ReleasedPlugin(string Name, string Version, string Sha256, IReadOnlyList<PluginFileHash> Files);

/// <summary>What comparing two releases' plugins found.</summary>
/// <param name="Errors">Reasons to refuse the release.</param>
/// <param name="Notes">Everything else worth printing.</param>
internal sealed record PluginCheck(IReadOnlyList<string> Errors, IReadOnlyList<string> Notes);

/// <summary>
/// Describes a release's plugin folders, writes and reads <c>plugins.json</c>, and enforces the
/// rule that a plugin's version changes exactly when its files do.
/// </summary>
/// <remarks>
/// <para>
/// The rule exists because a plugin version is a promise about bytes: the loader trusts a shipped
/// plugin by its folder hash, and a user reading "HyperXHid 1.0.0" in two releases should be
/// looking at the same files. So a hash that changed with the version unchanged fails the release,
/// and so does a version that changed with the hash unchanged, because nothing that was shipped
/// differs and the new number would claim otherwise. The second cannot happen by accident while the
/// version is compiled into the plugin assembly, so seeing it means something is wrong.
/// </para>
/// <para>
/// A plugin that is not in the previous release is new and passes. One that has gone is only noted.
/// </para>
/// </remarks>
internal static class PluginRelease
{
    private const string CandidatePattern = "Dongled.Plugin.*.dll";
    private const int FormatVersion = 1;

    /// <summary>Describe every plugin folder under a staged <c>Plugins\</c> directory.</summary>
    public static IReadOnlyList<ReleasedPlugin> Describe(string pluginsRoot)
    {
        var plugins = new List<ReleasedPlugin>();

        var directories = Directory.GetDirectories(pluginsRoot);
        Array.Sort(directories, StringComparer.Ordinal);

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            var candidates = Directory.GetFiles(directory, CandidatePattern, SearchOption.TopDirectoryOnly);
            if (candidates.Length != 1)
            {
                throw new ArgumentException(
                    $"'{name}' has {candidates.Length} files matching {CandidatePattern}; a plugin folder needs exactly one.");
            }

            var version = FileVersionInfo.GetVersionInfo(candidates[0]).ProductVersion;
            if (string.IsNullOrWhiteSpace(version))
            {
                throw new ArgumentException($"'{Path.GetFileName(candidates[0])}' carries no version.");
            }

            var manifest = PluginManifest.Compute(directory);
            plugins.Add(new ReleasedPlugin(name, version, manifest.Sha256, manifest.Files));
        }

        return plugins;
    }

    /// <summary>Check this release's plugins against the previous release's.</summary>
    public static PluginCheck Compare(IReadOnlyList<ReleasedPlugin> previous, IReadOnlyList<ReleasedPlugin> current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var errors = new List<string>();
        var notes = new List<string>();
        var before = previous.ToDictionary(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in current)
        {
            if (!before.Remove(plugin.Name, out var old))
            {
                notes.Add($"{plugin.Name} {plugin.Version}: new in this release.");
                continue;
            }

            var sameFiles = string.Equals(old.Sha256, plugin.Sha256, StringComparison.OrdinalIgnoreCase);
            var sameVersion = string.Equals(old.Version, plugin.Version, StringComparison.Ordinal);

            if (sameFiles && sameVersion)
            {
                notes.Add($"{plugin.Name} {plugin.Version}: unchanged.");
            }
            else if (!sameFiles && sameVersion)
            {
                errors.Add(
                    $"{plugin.Name} changed since the previous release but its version is still {plugin.Version}. "
                    + $"Differing files: {string.Join(", ", DifferingFiles(old, plugin))}. "
                    + $"Bump <Version> in plugins/Dongled.Plugin.{plugin.Name}/Dongled.Plugin.{plugin.Name}.csproj. "
                    + "If the change was not intended, the build is not reproducible: check that nothing per-build "
                    + "(a version, a path, a timestamp) reaches the plugin.");
            }
            else if (sameFiles)
            {
                errors.Add(
                    $"{plugin.Name}'s version changed from {old.Version} to {plugin.Version} but its files are "
                    + "byte-identical to the previous release's. A version is a promise about the files; revert the bump.");
            }
            else if (!IsNewer(plugin.Version, old.Version))
            {
                errors.Add(
                    $"{plugin.Name} changed and its version went from {old.Version} to {plugin.Version}, which is not higher.");
            }
            else
            {
                notes.Add($"{plugin.Name}: {old.Version} -> {plugin.Version}.");
            }
        }

        foreach (var gone in before.Values)
        {
            notes.Add($"{gone.Name} {gone.Version}: no longer shipped.");
        }

        return new PluginCheck(errors, notes);
    }

    /// <summary>Serialize a release's plugins as <c>plugins.json</c>.</summary>
    /// <remarks>
    /// The same document is attached to the release, for the next release to compare against, and
    /// compiled into Dongled.Core as the list of plugins to trust without an approval
    /// (see <see cref="BundledPluginTrust"/>, which reads <c>name</c> and <c>sha256</c>).
    /// </remarks>
    public static string Write(IReadOnlyList<ReleasedPlugin> plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteStartArray("plugins");
            foreach (var plugin in plugins)
            {
                writer.WriteStartObject();
                writer.WriteString("name", plugin.Name);
                writer.WriteString("version", plugin.Version);
                writer.WriteString("sha256", plugin.Sha256);
                writer.WriteStartArray("files");
                foreach (var file in plugin.Files)
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", file.RelativePath);
                    writer.WriteString("sha256", file.Sha256);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>Read a previous release's <c>plugins.json</c>.</summary>
    public static IReadOnlyList<ReleasedPlugin> Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("formatVersion", out var format) || format.GetInt32() != FormatVersion)
        {
            throw new ArgumentException($"The previous plugins.json is not format version {FormatVersion}.");
        }

        var plugins = new List<ReleasedPlugin>();
        foreach (var plugin in root.GetProperty("plugins").EnumerateArray())
        {
            var files = new List<PluginFileHash>();
            if (plugin.TryGetProperty("files", out var fileArray))
            {
                foreach (var file in fileArray.EnumerateArray())
                {
                    files.Add(new PluginFileHash(
                        file.GetProperty("path").GetString()!,
                        file.GetProperty("sha256").GetString()!));
                }
            }

            plugins.Add(new ReleasedPlugin(
                plugin.GetProperty("name").GetString()!,
                plugin.GetProperty("version").GetString()!,
                plugin.GetProperty("sha256").GetString()!,
                files));
        }

        return plugins;
    }

    private static IEnumerable<string> DifferingFiles(ReleasedPlugin old, ReleasedPlugin current)
    {
        var before = old.Files.ToDictionary(file => file.RelativePath, file => file.Sha256, StringComparer.Ordinal);
        var after = current.Files.ToDictionary(file => file.RelativePath, file => file.Sha256, StringComparer.Ordinal);

        return before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Where(path => !before.TryGetValue(path, out var a)
                || !after.TryGetValue(path, out var b)
                || !string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);
    }

    /// <summary>Whether one version is higher, comparing the numeric part and ignoring any suffix.</summary>
    private static bool IsNewer(string candidate, string baseline) =>
        Version.TryParse(NumericPart(candidate), out var a)
        && Version.TryParse(NumericPart(baseline), out var b)
        && a > b;

    private static string NumericPart(string version)
    {
        var end = version.IndexOfAny(['-', '+']);
        return end < 0 ? version : version[..end];
    }
}
