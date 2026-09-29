using System.Collections.Generic;
using System.Linq;
using Dongled.Core.Audio;
using Dongled.Core.Logging;
using Dongled.Core.Pipeline;

namespace Dongled.App.Presentation;

/// <summary>
/// Turns an engine log line into a sentence for the Status page's recent activity.
/// </summary>
/// <remarks>
/// <para>
/// The engine's messages are the right sentences already — "{SourceId} disconnected: returning the
/// Media role to {DeviceId}" — but they are rendered with identifiers in them, and the UI never
/// shows those. The fix is not to match on templates and rewrite each one by hand: that
/// couples this class to Core's exact wording, and a template Core adds later would silently
/// arrive on screen with a GUID in it.
/// </para>
/// <para>
/// Instead this works on the structured values. Every identifier-shaped field is replaced in the
/// rendered text by the name it resolves to, whatever the template was. A field that cannot be
/// resolved is replaced by a phrase, never left as it is, so a template this class has never seen
/// is still safe. <see cref="ContainsEndpointIdentifier"/> is the backstop for anything that
/// escapes both.
/// </para>
/// </remarks>
internal static class ActivityComposer
{
    /// <summary>
    /// Template field names whose values are identifiers rather than anything readable. Matched by
    /// name because that is what the message template declares, and by suffix so a field Core adds
    /// later — <c>TargetDeviceId</c>, say — is covered without an edit here.
    /// </summary>
    private static readonly string[] DeviceFieldSuffixes = ["DeviceId", "EndpointId"];

    private static readonly string[] SourceFieldSuffixes = ["SourceId"];

    /// <summary>
    /// Whether a log line belongs on the Status page. The engine and the policy explain what the app
    /// did; everything else is diagnostics for the Logs page.
    /// </summary>
    /// <param name="entry">The line to judge.</param>
    public static bool IsActivity(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.Level >= Microsoft.Extensions.Logging.LogLevel.Information
            && (entry.Category.StartsWith("Dongled.Core.Policy", StringComparison.Ordinal)
                || entry.Category.StartsWith("Dongled.Core.Engine", StringComparison.Ordinal));
    }

    /// <summary>
    /// The line rewritten with names in place of identifiers, or null if it cannot be made safe to
    /// show.
    /// </summary>
    /// <param name="entry">The line to rewrite.</param>
    /// <param name="endpoints">The current endpoint enumeration, for naming devices.</param>
    /// <param name="sources">What providers currently publish, for naming sources.</param>
    /// <param name="config">Configuration, for the last known name of anything absent.</param>
    public static string? Compose(
        LogEntry entry,
        IReadOnlyList<AudioEndpoint> endpoints,
        IReadOnlyList<SourceState> sources,
        Core.Configuration.AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(config);

        var text = entry.Message;

        foreach (var (key, value) in entry.State)
        {
            if (string.Equals(key, "{OriginalFormat}", StringComparison.Ordinal))
            {
                continue;
            }

            var identifier = value?.ToString();
            if (string.IsNullOrWhiteSpace(identifier) || !text.Contains(identifier, StringComparison.Ordinal))
            {
                continue;
            }

            if (EndsWithAny(key, DeviceFieldSuffixes))
            {
                text = text.Replace(identifier, NameDevice(endpoints, config, identifier), StringComparison.Ordinal);
            }
            else if (EndsWithAny(key, SourceFieldSuffixes))
            {
                text = text.Replace(identifier, NameSource(sources, config, identifier), StringComparison.Ordinal);
            }
        }

        // Backstop. If a Windows endpoint identifier is still in the text, some template put one
        // somewhere this method did not look, and showing it would breach 13.3. Dropping the line
        // costs one row of history; showing it costs the guarantee.
        return ContainsEndpointIdentifier(text) ? null : text;
    }

    /// <summary>
    /// Whether text still holds something shaped like a Windows endpoint identifier, which always
    /// begins with a container GUID in braces followed by a dot.
    /// </summary>
    /// <param name="text">The text to check.</param>
    public static bool ContainsEndpointIdentifier(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Contains("}.{", StringComparison.Ordinal)
            || text.Contains("{0.0.", StringComparison.Ordinal);
    }

    private static bool EndsWithAny(string key, string[] suffixes)
    {
        foreach (var suffix in suffixes)
        {
            if (key.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string NameDevice(
        IReadOnlyList<AudioEndpoint> endpoints,
        Core.Configuration.AppConfig config,
        string identifier)
    {
        var lastKnown = config.Rules
            .SelectMany(static rule => new[]
            {
                (Id: rule.Target.DeviceId, Name: rule.Target.LastKnownName),
                (Id: rule.OnDisconnect.FallbackDeviceId ?? string.Empty, Name: rule.OnDisconnect.FallbackLastKnownName),
            })
            .Where(pair => string.Equals(pair.Id, identifier, StringComparison.OrdinalIgnoreCase))
            .Select(static pair => pair.Name)
            .FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name));

        return DisplayNames.ForDevice(endpoints, identifier, lastKnown);
    }

    private static string NameSource(
        IReadOnlyList<SourceState> sources,
        Core.Configuration.AppConfig config,
        string identifier)
    {
        var lastKnown = config.Rules
            .Where(rule => string.Equals(rule.Source.Id, identifier, StringComparison.OrdinalIgnoreCase))
            .Select(static rule => rule.Source.LastKnownName)
            .FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name));

        return DisplayNames.ForSource(sources, identifier, lastKnown);
    }
}
