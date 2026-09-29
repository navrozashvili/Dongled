using System.Text.Json;
using System.Text.Json.Serialization;
using Dongled.Abstractions;

namespace Dongled.Plugin.Logitech;

/// <summary>
/// What the plugin reads out of <c>logitech-ghub.json</c>, after validation and clamping.
/// </summary>
/// <remarks>
/// <para>
/// The file lives in the app's data folder, not beside the plugin. A plugin directory is the trust unit
/// and is hashed whole, so a file written there re-blocks the plugin until the user approves it again;
/// and a writable file beside a hash-pinned DLL is a tampering surface.
/// </para>
/// <para>
/// The path deliberately duplicates <c>Dongled.Core.Configuration.StoragePaths</c>: a
/// plugin references only the SDK, so it cannot use Core. The plugin only ever
/// <em>reads</em> this file and never creates it.
/// </para>
/// </remarks>
internal sealed record LogitechGhubOptions(
    Uri Url,
    TimeSpan ReconnectDelay,
    TimeSpan RefreshInterval,
    TimeSpan KeepAliveInterval,
    TimeSpan DisconnectGrace,
    IReadOnlyList<string> ConnectedStates)
{
    // CA1869 makes a JsonSerializerOptions created per call a build error, so it is hoisted.
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly string[] DefaultConnectedStates = ["ACTIVE"];

    /// <summary>The values used when there is no config file, which is the normal case.</summary>
    public static LogitechGhubOptions Default { get; } = new(
        Url: new Uri("ws://localhost:9010/"),
        ReconnectDelay: TimeSpan.FromSeconds(3),
        RefreshInterval: TimeSpan.FromSeconds(5),
        KeepAliveInterval: TimeSpan.FromSeconds(30),
        DisconnectGrace: TimeSpan.FromSeconds(8),
        ConnectedStates: DefaultConnectedStates);

    /// <summary>Where the file is looked for. Read, never written.</summary>
    public static string DefaultConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dongled",
        "logitech-ghub.json");

    /// <summary>
    /// Whether <paramref name="url"/> is a websocket address on this machine's loopback interface.
    /// The URL comes from a user-editable file, so without this check it could point the plugin
    /// anywhere on the network.
    /// </summary>
    /// <remarks>
    /// <see cref="Uri.IsLoopback"/> covers <c>localhost</c>, <c>127.0.0.1</c> and <c>[::1]</c>, and is a
    /// host comparison rather than a prefix match, so <c>localhost.example.com</c> is refused.
    /// </remarks>
    public static bool TryParseLoopback(string? url, out Uri? parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var candidate))
        {
            return false;
        }

        if (!candidate.IsLoopback)
        {
            return false;
        }

        if (!string.Equals(candidate.Scheme, "ws", StringComparison.Ordinal)
            && !string.Equals(candidate.Scheme, "wss", StringComparison.Ordinal))
        {
            return false;
        }

        parsed = candidate;
        return true;
    }

    /// <summary>
    /// Read the file at <paramref name="path"/>. Never throws and never creates anything: a missing,
    /// unreadable or malformed file means <see cref="Default"/>, and anything out of range is clamped.
    /// </summary>
    public static LogitechGhubOptions Load(string path, IProviderLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        FileContents? contents;
        try
        {
            if (!File.Exists(path))
            {
                return Default;
            }

            contents = JsonSerializer.Deserialize<FileContents>(File.ReadAllText(path), ReadOptions);
        }
        catch (Exception ex)
        {
            Warn(logger, $"Could not read logitech-ghub.json, so the defaults are being used. {ex.Message}");
            return Default;
        }

        if (contents is null)
        {
            Warn(logger, "logitech-ghub.json parsed as null, so the defaults are being used.");
            return Default;
        }

        var url = Default.Url;
        if (contents.Url is not null)
        {
            if (TryParseLoopback(contents.Url, out var parsed))
            {
                url = parsed!;
            }
            else
            {
                Warn(
                    logger,
                    $"The url in logitech-ghub.json is not a loopback websocket address, so {Default.Url} is being used instead.");
            }
        }

        var states = contents.ConnectedStates?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new LogitechGhubOptions(
            Url: url,
            ReconnectDelay: Seconds(contents.ReconnectSeconds, Default.ReconnectDelay, 1, 300),
            RefreshInterval: Seconds(contents.RefreshSeconds, Default.RefreshInterval, 2, 3600),
            KeepAliveInterval: Seconds(contents.KeepAliveSeconds, Default.KeepAliveInterval, 5, 300),
            DisconnectGrace: Seconds(contents.DisconnectGraceSeconds, Default.DisconnectGrace, 0, 300),

            // An empty list would mean no device is ever connected, which is never what anyone meant.
            ConnectedStates: states is null || states.Length == 0 ? DefaultConnectedStates : states);
    }

    private static TimeSpan Seconds(int? value, TimeSpan fallback, int minimum, int maximum) =>
        value is null ? fallback : TimeSpan.FromSeconds(Math.Clamp(value.Value, minimum, maximum));

    private static void Warn(IProviderLogger logger, string message)
    {
        if (logger.IsEnabled(ProviderLogLevel.Warning))
        {
            logger.Log(ProviderLogLevel.Warning, message);
        }
    }

    /// <summary>
    /// The file's shape. Every member is nullable so an absent key is distinguishable from a supplied
    /// zero, which is what makes "unrecognised fields are ignored, recognised ones are clamped" work.
    /// </summary>
    private sealed class FileContents
    {
        public string? Url { get; init; }

        public int? ReconnectSeconds { get; init; }

        public int? RefreshSeconds { get; init; }

        public int? KeepAliveSeconds { get; init; }

        public int? DisconnectGraceSeconds { get; init; }

        public IReadOnlyList<string>? ConnectedStates { get; init; }
    }
}
