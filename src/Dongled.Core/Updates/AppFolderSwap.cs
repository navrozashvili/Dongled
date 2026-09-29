using System.IO;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Updates;

/// <summary>
/// Replaces the files of a running app with a new version's, in place, without a helper process.
/// </summary>
/// <remarks>
/// <para>
/// Windows will not delete the executable or a DLL a running process has loaded, but it will
/// rename one within the same volume. So every file about to be replaced is first moved aside into
/// <see cref="BackupDirectoryName"/> inside the app folder, and only then is the new file moved into
/// its place. The running process keeps executing the moved-aside images; the next process starts
/// from the new ones.
/// </para>
/// <para>
/// A folder rather than a <c>.old</c> suffix beside each file, so that nothing left over ever sits
/// inside a plugin's folder, where it would change the hash that plugin was approved under and be
/// listed by the loader until someone deleted it.
/// </para>
/// <para>
/// What is replaced: every file the new version ships at the same relative path, and the whole
/// contents of each plugin folder it bundles, so a file a bundled plugin no longer ships does not
/// linger beside the new ones. Plugin folders the new version does not ship are the user's, and
/// are not touched. Files outside the app folder, including everything under the per-user data
/// folder, are never touched.
/// </para>
/// <para>
/// If any step fails, everything done so far is undone in reverse order before the exception
/// reaches the caller, so the app folder is again exactly what was running.
/// </para>
/// </remarks>
public static class AppFolderSwap
{
    /// <summary>Where replaced files are moved, inside the app folder. Deleted by the next start.</summary>
    public const string BackupDirectoryName = ".update-backup";

    /// <summary>The folder bundled plugins ship in, relative to the app folder.</summary>
    public const string PluginsDirectoryName = "Plugins";

    /// <summary>Check that the app folder, and its plugins folder if there is one, can be written.</summary>
    /// <exception cref="UpdateException">One of them cannot.</exception>
    public static void EnsureWritable(string appDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);

        Probe(appDirectory, appDirectory);

        var plugins = Path.Combine(appDirectory, PluginsDirectoryName);
        if (Directory.Exists(plugins))
        {
            Probe(plugins, appDirectory);
        }
    }

    /// <summary>Move the contents of <paramref name="newContent"/> into <paramref name="appDirectory"/>.</summary>
    /// <param name="newContent">The extracted new version. Emptied as its files are moved out.</param>
    /// <param name="appDirectory">The folder the running app was started from.</param>
    /// <param name="logger">Where each failure is recorded.</param>
    /// <returns>How many files were put in place.</returns>
    /// <exception cref="UpdateException">
    /// A step failed. Everything already done has been undone, unless the message says otherwise.
    /// </exception>
    public static int Apply(string newContent, string appDirectory, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newContent);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        var appRoot = Path.GetFullPath(appDirectory);
        var contentRoot = Path.GetFullPath(newContent);

        var incoming = Directory
            .EnumerateFiles(contentRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(contentRoot, path))
            .Where(relative => !IsUnder(relative, BackupDirectoryName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var outgoing = incoming
            .Where(relative => File.Exists(Path.Combine(appRoot, relative)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in BundledPlugins(contentRoot))
        {
            var existing = Path.Combine(appRoot, PluginsDirectoryName, plugin);
            if (!Directory.Exists(existing))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(existing, "*", SearchOption.AllDirectories))
            {
                outgoing.Add(Path.GetRelativePath(appRoot, file));
            }
        }

        // A fresh folder per update, so a leftover the last cleanup could not delete never
        // collides with this one.
        var backupRoot = Path.Combine(appRoot, BackupDirectoryName, DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture));
        var journal = new Stack<Action>();

        try
        {
            foreach (var relative in incoming.Union(outgoing, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                var target = Path.Combine(appRoot, relative);

                if (outgoing.Contains(relative))
                {
                    var aside = Path.Combine(backupRoot, relative);
                    CreateDirectory(Path.GetDirectoryName(aside)!, journal);
                    File.Move(target, aside);
                    journal.Push(() => File.Move(aside, target));
                }

                if (incoming.Contains(relative))
                {
                    var source = Path.Combine(contentRoot, relative);
                    CreateDirectory(Path.GetDirectoryName(target)!, journal);
                    File.Move(source, target);
                    journal.Push(() => File.Move(target, source));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Replacing the app's files failed; undoing the {Count} step(s) already taken.", journal.Count);

            var undone = Undo(journal, logger);

            throw undone
                ? new UpdateException($"Dongled could not replace its own files, so nothing was changed. {ex.Message}", ex)
                : new UpdateException(
                    $"Dongled could not replace its own files, and could not put every original back. Download the release again from {UpdateEndpoints.ReleasesPage} and replace the folder by hand.",
                    ex);
        }

        return incoming.Count;
    }

    /// <summary>Delete what an earlier update moved aside.</summary>
    /// <returns>Whether nothing is left.</returns>
    /// <remarks>
    /// Best effort: a file that is still in use, because the process that was replaced has not
    /// quite finished exiting, stays for the next attempt.
    /// </remarks>
    public static bool CleanUp(string appDirectory, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        var backup = Path.Combine(appDirectory, BackupDirectoryName);
        if (!Directory.Exists(backup))
        {
            return true;
        }

        try
        {
            Directory.Delete(backup, recursive: true);
            logger.LogInformation("Removed the files the last update replaced.");
            return true;
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex, "The files the last update replaced could not all be removed yet.");
            }

            return false;
        }
    }

    private static IEnumerable<string> BundledPlugins(string contentRoot)
    {
        var plugins = Path.Combine(contentRoot, PluginsDirectoryName);

        return Directory.Exists(plugins)
            ? Directory.EnumerateDirectories(plugins).Select(Path.GetFileName).OfType<string>()
            : [];
    }

    private static bool IsUnder(string relative, string directory) =>
        relative.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Create a directory and every missing parent, recording each so an undo removes it again.</summary>
    private static void CreateDirectory(string directory, Stack<Action> journal)
    {
        var missing = new Stack<string>();
        for (var current = directory; !string.IsNullOrEmpty(current) && !Directory.Exists(current); current = Path.GetDirectoryName(current))
        {
            missing.Push(current);
        }

        while (missing.Count > 0)
        {
            var created = missing.Pop();
            Directory.CreateDirectory(created);

            // Only if empty: by the time the undo reaches this entry, everything moved in after it
            // has been moved out again. An empty folder that cannot be removed is harmless, so it
            // does not count as a failed undo.
            journal.Push(() => DeleteIfEmpty(created));
        }
    }

    private static void DeleteIfEmpty(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool Undo(Stack<Action> journal, ILogger logger)
    {
        var clean = true;

        while (journal.Count > 0)
        {
            try
            {
                journal.Pop()();
            }
            catch (Exception ex)
            {
                clean = false;
                logger.LogError(ex, "A step of undoing a failed update could not be undone.");
            }
        }

        return clean;
    }

    private static void Probe(string directory, string appDirectory)
    {
        var probe = Path.Combine(directory, ".dongled-write-test-" + Guid.NewGuid().ToString("N"));

        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new UpdateException(
                $"Dongled cannot update itself because it cannot write to the folder it runs from ({appDirectory}). This usually means it is under Program Files. Move the Dongled folder somewhere you own, such as your user folder, or download the new version from {UpdateEndpoints.ReleasesPage} and replace the folder yourself.",
                ex);
        }
    }
}
