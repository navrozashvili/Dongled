namespace Dongled.Core.Configuration;

/// <summary>
/// The default endpoints captured before a rule switched away from them.
/// </summary>
/// <param name="MultimediaId">Endpoint that held the multimedia role, or null if it was not captured.</param>
/// <param name="CommunicationsId">Endpoint that held the communications role, or null if it was not captured.</param>
/// <param name="CapturedUtc">When the capture happened.</param>
public sealed record PreviousDefault(string? MultimediaId, string? CommunicationsId, DateTimeOffset CapturedUtc);

/// <summary>
/// Runtime state the app accumulates: the switching engine's captured previous devices, and what
/// the last update check found.
/// </summary>
/// <remarks>
/// Kept apart from configuration so that no writer can discard another's changes by saving a
/// stale copy. The two sections here have one writer each, and both go through the same store,
/// which reads through to disk and serializes every read-modify-write, so neither can revert the
/// other.
/// </remarks>
public sealed class AppState
{
    /// <summary>Schema version of this file. Bumped only for a breaking change.</summary>
    /// <remarks>
    /// A file whose <c>schemaVersion</c> is higher than the running app understands is treated as
    /// unreadable: the app falls back to defaults and logs that it did so, and leaves the file on
    /// disk rather than overwriting it, so running an older build does not silently destroy a
    /// newer configuration. No migration code is shipped.
    /// </remarks>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Previously-default endpoints, keyed by the source identifier whose rule captured them.
    /// Ordinal-ignore-case because Windows does not guarantee stable casing for endpoint
    /// identifiers.
    /// </summary>
    /// <remarks>
    /// The comparer is not preserved when this object is deserialized. The property is settable,
    /// so System.Text.Json assigns a fresh dictionary through the setter rather than populating
    /// the one this initializer created, and the ordinal-ignore-case comparer is discarded along
    /// with it; lookups on the loaded instance are case sensitive. Whatever loads this file is
    /// therefore responsible for rebuilding the dictionary with
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> after the load.
    /// </remarks>
    public Dictionary<string, PreviousDefault> PreviousDefaults { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the last update check found. Written only by the update service.</summary>
    /// <remarks>
    /// Additive, so the schema version is unchanged: a file without it reads as never checked,
    /// and an older build ignores it.
    /// </remarks>
    public UpdateCheckState? Updates { get; set; }
}
