using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Configuration;

/// <summary>
/// Stores captured previous defaults, and what the last update check found, in a single JSON file
/// kept apart from user configuration.
/// </summary>
/// <remarks>
/// <para>
/// Reads through to disk on every call rather than caching. The file is tiny and touched only
/// when a rule fires or an update check finishes, and a fresh read means an external edit or a
/// reset is picked up without a restart.
/// </para>
/// <para>
/// The switching engine and the update service each own one section and both write through this
/// one instance. Every read-modify-write holds <see cref="_gate"/>, so a capture on the engine's
/// thread and a check finishing on the thread pool cannot interleave and lose one of the two.
/// </para>
/// </remarks>
public sealed class StateStore : IStateStore, IUpdateStateStore
{
    /// <summary>
    /// Highest schema version this build understands. A file declaring a higher one is treated
    /// as unreadable rather than guessed at.
    /// </summary>
    private const int CurrentSchemaVersion = 1;

    private readonly string _filePath;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StateStore> _logger;
    private readonly Lock _gate = new();

    /// <param name="filePath">Full path to the state file.</param>
    /// <param name="timeProvider">Clock used to stamp captures, so tests need not sleep.</param>
    /// <param name="logger">Where to report an unreadable file.</param>
    public StateStore(string filePath, TimeProvider timeProvider, ILogger<StateStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _filePath = filePath;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public PreviousDefault? GetPrevious(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        return Load().PreviousDefaults.GetValueOrDefault(sourceId);
    }

    /// <inheritdoc />
    public void CapturePrevious(string sourceId, string? multimediaId, string? communicationsId, string targetDeviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceId);

        // A role already on the target must not be remembered as its own previous, or the
        // return would restore to the device the rule switched away from and appear to do
        // nothing at all.
        var multimedia = SameDevice(multimediaId, targetDeviceId) ? null : NullIfBlank(multimediaId);
        var communications = SameDevice(communicationsId, targetDeviceId) ? null : NullIfBlank(communicationsId);

        if (multimedia is null && communications is null)
        {
            // Guarded because CA1873 objects to the argument array a Debug-level call allocates
            // whether or not Debug is enabled, and this call sits on the switching path.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Nothing to capture for {SourceId}: both roles already point at the target device.",
                    sourceId);
            }

            return;
        }

        lock (_gate)
        {
            var state = Load();
            state.PreviousDefaults[sourceId] = new PreviousDefault(multimedia, communications, _timeProvider.GetUtcNow());
            Save(state);
        }
    }

    /// <inheritdoc />
    public UpdateCheckState LoadUpdateState()
    {
        lock (_gate)
        {
            return Load().Updates ?? new UpdateCheckState();
        }
    }

    /// <inheritdoc />
    public void SaveUpdateState(UpdateCheckState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (_gate)
        {
            var current = Load();
            current.Updates = state;
            Save(current);
        }
    }

    /// <inheritdoc />
    public void ClearPrevious(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        lock (_gate)
        {
            var state = Load();
            if (state.PreviousDefaults.Remove(sourceId))
            {
                Save(state);
            }
        }
    }

    private static bool SameDevice(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Whether an entry read from the file is one the write path could have produced. Repairs the
    /// two shapes a hand-edited file can express and this store never writes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// System.Text.Json ignores nullable reference annotations, so <c>{"src": null}</c> puts a
    /// genuine null inside a dictionary whose value type is non-nullable, and the save that
    /// follows round-trips it back to disk. Every consumer types the value as non-nullable, so
    /// the null would surface a long way from the file that caused it.
    /// </para>
    /// <para>
    /// <c>{"src": {}}</c> loads as an entry naming no device at all. The write path refuses to
    /// record that shape on purpose, because an entry holding nothing to come back to makes a
    /// later restore look possible when it is not.
    /// </para>
    /// </remarks>
    private static bool IsWorthKeeping(PreviousDefault? entry) =>
        entry is not null
        && (!string.IsNullOrWhiteSpace(entry.MultimediaId) || !string.IsNullOrWhiteSpace(entry.CommunicationsId));

    private AppState Load()
    {
        string? json;
        try
        {
            json = AtomicFile.ReadAllTextOrNull(_filePath);
        }
        catch (Exception ex)
        {
            // Deliberately broad: losing captured state is bad, but failing to start is worse.
            _logger.LogWarning(ex, "Could not read the state file at {Path}. Starting from empty state.", _filePath);
            return new AppState();
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return new AppState();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<AppState>(json, JsonSetup.Options);
            if (parsed is null)
            {
                return new AppState();
            }

            // Same rule as the configuration file, and the one AppState.SchemaVersion's doc
            // comment promises: a newer file is unreadable rather than guessed at, and is left
            // on disk so a downgrade does not destroy it. Losing captured previous devices is
            // recoverable; restoring to a device a misread file named is not.
            if (parsed.SchemaVersion > CurrentSchemaVersion)
            {
                _logger.LogWarning(
                    "The state file at {Path} declares schema version {FileVersion}, but this build understands only {SupportedVersion}. Starting from empty state; the file is left unchanged.",
                    _filePath,
                    parsed.SchemaVersion,
                    CurrentSchemaVersion);
                return new AppState();
            }

            // The serializer builds a plain dictionary regardless of the comparer on the
            // property initializer, so rebuild it to keep lookups case insensitive. This is
            // the rebuild AppState.PreviousDefaults' doc comment makes the storage layer
            // responsible for; without it a captured previous is not found when Windows
            // reports the endpoint identifier with different casing.
            return new AppState
            {
                SchemaVersion = parsed.SchemaVersion,
                PreviousDefaults = (parsed.PreviousDefaults ?? [])
                    .Where(entry => IsWorthKeeping(entry.Value))
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase),

                // Carried through so a capture does not erase what the last update check found.
                Updates = parsed.Updates,
            };
        }
        catch (Exception ex)
        {
            // Deliberately broad, like the read above, and for the same reason the configuration
            // store gives: no failure to make sense of this file may stop the app starting.
            // Malformed JSON raises JsonException, but this block can fail for reasons that have
            // nothing to do with the syntax of the file. Two are real. A document holding the
            // same source twice under different casing, which JSON permits and the serializer
            // accepts, makes the ordinal-ignore-case rebuild throw ArgumentException on the
            // duplicate key. And enabling PublishAot or PublishTrimmed makes every
            // reflection-based Deserialize call throw InvalidOperationException, so a build
            // property must not be able to make the app unlaunchable. Anything that goes wrong
            // here degrades to empty state instead.
            _logger.LogWarning(
                ex,
                "The state file at {Path} could not be read as state. Starting from empty state; captured previous devices are lost.",
                _filePath);
            return new AppState();
        }
    }

    private void Save(AppState state)
    {
        // Load leaves a newer file alone, but returns empty state, so the first capture after a
        // downgrade would write this build's shape straight over it. Without this the promise
        // AppState.SchemaVersion's doc comment makes is honoured on the read side only.
        SchemaGuard.PreserveNewerFile(_filePath, CurrentSchemaVersion, _logger);

        // No try/catch here on purpose, as in the configuration store: a failed write must reach
        // the caller, including the UnauthorizedAccessException that a catch narrowed to
        // IOException would miss.
        AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(state, JsonSetup.Options));
    }
}
