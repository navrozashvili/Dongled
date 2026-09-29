using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Logging;

/// <summary>
/// One recorded log line, kept in memory so the Logs page can tail it and the Status page can
/// build its activity list.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="State"/> is the part that matters and the reason this is not just a string. The UI
/// never shows a raw source identifier or device GUID, and the engine's own messages are full of
/// them: "{SourceId} connected: giving {Roles} to {DeviceId}". Rendering <see cref="Message"/> on
/// the Status page would put those identifiers on screen.
/// </para>
/// <para>
/// Keeping the structured values separately lets a caller read <c>SourceId</c> as a value,
/// resolve it to a display name, and compose its own sentence — so the identifier is used as a
/// lookup key and never as text on screen.
/// </para>
/// </remarks>
/// <param name="Timestamp">When the line was recorded.</param>
/// <param name="Level">Severity, used by the Logs page's filter.</param>
/// <param name="Category">The logger category, for example <c>Provider.hyperx-hid</c>.</param>
/// <param name="Message">The rendered message. May contain identifiers; see the remarks.</param>
/// <param name="Exception">The exception's full text, or null if there was none.</param>
/// <param name="State">
/// The message template's named values when the logger supplied them, otherwise empty. Includes
/// the <c>{OriginalFormat}</c> entry that <see cref="ILogger"/> adds.
/// </param>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    string? Exception,
    IReadOnlyList<KeyValuePair<string, object?>> State)
{
    /// <summary>
    /// The value the message template recorded under <paramref name="name"/>, or null if the
    /// template had no such field.
    /// </summary>
    /// <param name="name">A template field name, for example <c>SourceId</c>.</param>
    public string? Field(string name)
    {
        foreach (var pair in State)
        {
            if (string.Equals(pair.Key, name, StringComparison.Ordinal))
            {
                return pair.Value?.ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// The message template itself, which identifies what was logged without depending on the
    /// values substituted into it. Null when the logger supplied no structured state.
    /// </summary>
    /// <remarks>
    /// This is what a caller matches on to recognise a particular event, rather than matching the
    /// rendered message — the rendered form changes with every identifier in it.
    /// </remarks>
    public string? Template => State
        .Where(static pair => string.Equals(pair.Key, "{OriginalFormat}", StringComparison.Ordinal))
        .Select(static pair => pair.Value?.ToString())
        .FirstOrDefault();

    /// <summary>The short severity label the Logs page shows.</summary>
    public string LevelLabel => Level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT",
        _ => "NONE",
    };
}
