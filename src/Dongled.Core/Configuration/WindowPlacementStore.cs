using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Configuration;

/// <summary>Stores the window's last placement in a single JSON file.</summary>
/// <remarks>
/// Reads through to disk on every call rather than caching, like <see cref="StateStore"/>. It is
/// read once at startup and written when the window is put away, so there is nothing to cache for.
/// </remarks>
public sealed class WindowPlacementStore : IWindowPlacementStore
{
    /// <summary>
    /// Highest schema version this build understands. A file declaring a higher one is treated
    /// as unreadable rather than guessed at.
    /// </summary>
    private const int CurrentSchemaVersion = 1;

    private readonly string _filePath;
    private readonly ILogger<WindowPlacementStore> _logger;

    /// <param name="filePath">Full path to the placement file.</param>
    /// <param name="logger">Where to report a file that could not be read or written.</param>
    public WindowPlacementStore(string filePath, ILogger<WindowPlacementStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(logger);

        _filePath = filePath;
        _logger = logger;
    }

    /// <inheritdoc />
    public WindowPlacement? Load()
    {
        string? json;
        try
        {
            json = AtomicFile.ReadAllTextOrNull(_filePath);
        }
        catch (Exception ex)
        {
            // Deliberately broad, for the reason the other two stores give: no failure to read a
            // file may stop the app starting. Debug rather than Warning because a window that
            // opens in its default position is not a problem the user needs telling about.
            //
            // Guarded, here and at every other Debug call in this file, because CA1873 objects to
            // the argument array the call allocates whether or not Debug is enabled - the same
            // guard, for the same reason, as the one in StateStore.CapturePrevious.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Could not read the window placement file at {Path}. Using the default position.", _filePath);
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<WindowPlacement>(json, JsonSetup.Options);

            if (parsed is null)
            {
                return null;
            }

            // The same promise the other two files make: a newer file is not guessed at, and is
            // left on disk so a downgrade does not destroy it.
            if (parsed.SchemaVersion > CurrentSchemaVersion)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "The window placement file at {Path} declares schema version {FileVersion}, but this build understands only {SupportedVersion}. Using the default position; the file is left unchanged.",
                        _filePath,
                        parsed.SchemaVersion,
                        CurrentSchemaVersion);
                }

                return null;
            }

            return parsed;
        }
        catch (Exception ex)
        {
            // Broad for the same reasons the other stores document: malformed JSON is only one of
            // the ways this can fail, and a build property that turns off reflection-based
            // deserialization must not be able to make the app unlaunchable.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "The window placement file at {Path} could not be read. Using the default position.", _filePath);
            }

            return null;
        }
    }

    /// <inheritdoc />
    public void Save(WindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        try
        {
            // Load leaves a newer file alone but returns null, so the next save would write this
            // build's shape straight over it.
            SchemaGuard.PreserveNewerFile(_filePath, CurrentSchemaVersion, _logger);

            AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(placement, JsonSetup.Options));
        }
        catch (Exception ex)
        {
            // Swallowed on purpose, and this is the one store where that is right — see
            // IWindowPlacementStore. This runs while the window is being hidden or the process is
            // ending, so there is nowhere to report it and nothing that behaves differently; the
            // cost is that the window opens where it would on a first run. Broad rather than
            // IOException because UnauthorizedAccessException does not derive from it.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Could not write the window placement file at {Path}.", _filePath);
            }
        }
    }
}
