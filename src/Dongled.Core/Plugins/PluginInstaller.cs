using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Dongled.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Plugins;

/// <summary>What an incoming package means for the plugins directory as it stands.</summary>
public enum PluginInstallOutcome
{
    /// <summary>Nothing of this name is installed. Extract and approve.</summary>
    Fresh = 0,

    /// <summary>Byte for byte what is already there. Nothing to copy.</summary>
    AlreadyInstalled = 1,

    /// <summary>The same plugin, different files. Replacing discards its approval.</summary>
    Upgrade = 2,

    /// <summary>A different plugin already holds this folder name. Refused.</summary>
    Conflict = 3,
}

/// <summary>What an install or a removal did.</summary>
/// <param name="Succeeded">Whether the plugins directory now holds what was asked for.</param>
/// <param name="DirectoryName">The directory acted on.</param>
/// <param name="Sha256">
/// The hash of the installed directory, which is the value approval records. Null for a failure or
/// a removal.
/// </param>
/// <param name="Message">One sentence a user can act on.</param>
public sealed record PluginInstallResult(bool Succeeded, string DirectoryName, string? Sha256, string Message);

/// <summary>
/// A package, judged against the plugins directory as it currently stands.
/// </summary>
/// <remarks>
/// Owns the extracted package until it is either installed or disposed, so a dialog the user
/// cancels leaves nothing on disk.
/// </remarks>
public sealed class PluginInspection : IDisposable
{
    /// <param name="package">The extracted, hashed package this inspection owns until installed or disposed.</param>
    /// <param name="outcome">What this package means for the directory it names.</param>
    /// <param name="message">One sentence describing the outcome.</param>
    /// <param name="differingFiles">
    /// Which files differ from what is installed, for <see cref="PluginInstallOutcome.Upgrade"/>.
    /// Empty otherwise.
    /// </param>
    /// <param name="installedAssemblyName">
    /// The assembly name already in that folder, for <see cref="PluginInstallOutcome.Conflict"/>.
    /// Null otherwise.
    /// </param>
    /// <param name="isInstalledPluginLoaded">
    /// Whether the plugin being replaced is running. Its files are open, so it cannot be replaced
    /// until the app restarts.
    /// </param>
    internal PluginInspection(
        PluginPackage package,
        PluginInstallOutcome outcome,
        string message,
        IReadOnlyList<string> differingFiles,
        string? installedAssemblyName,
        bool isInstalledPluginLoaded)
    {
        Package = package;
        Outcome = outcome;
        Message = message;
        DifferingFiles = differingFiles;
        InstalledAssemblyName = installedAssemblyName;
        IsInstalledPluginLoaded = isInstalledPluginLoaded;
    }

    /// <summary>The extracted, hashed package this inspection owns until installed or disposed.</summary>
    internal PluginPackage Package { get; }

    /// <summary>What this package means for the directory it names.</summary>
    public PluginInstallOutcome Outcome { get; }

    /// <summary>The folder this would become, and its configuration key.</summary>
    public string DirectoryName => Package.DirectoryName;

    /// <summary>The candidate assembly's simple name.</summary>
    public string AssemblyName => Package.AssemblyName;

    /// <summary>The hash of the package as extracted.</summary>
    public string Sha256 => Package.Sha256;

    /// <summary>
    /// Which files differ from what is installed, for <see cref="PluginInstallOutcome.Upgrade"/>.
    /// Empty otherwise.
    /// </summary>
    public IReadOnlyList<string> DifferingFiles { get; }

    /// <summary>
    /// The assembly name already in that folder, for <see cref="PluginInstallOutcome.Conflict"/>.
    /// Null otherwise.
    /// </summary>
    public string? InstalledAssemblyName { get; }

    /// <summary>
    /// Whether the plugin being replaced is running. Its files are open, so it cannot be replaced
    /// until the app restarts.
    /// </summary>
    public bool IsInstalledPluginLoaded { get; }

    /// <summary>One sentence describing the outcome.</summary>
    public string Message { get; }

    /// <inheritdoc />
    public void Dispose() => Package.Dispose();
}

/// <summary>
/// Puts a plugin package into the plugins directory, and takes one out again.
/// </summary>
/// <remarks>
/// Nothing here grants trust: an installed plugin is exactly as unapproved as one a user copied in
/// with Explorer, and only the approval dialog writes <see cref="PluginConfig.ManifestSha256"/>.
/// </remarks>
public sealed class PluginInstaller
{
    private static readonly SearchValues<char> NotInAName =
        SearchValues.Create(Path.GetInvalidFileNameChars());

    private readonly IConfigStore _configStore;
    private readonly string _pluginsDirectory;
    private readonly string _stagingDirectory;
    private readonly Func<string, bool> _isLoaded;
    private readonly ILogger<PluginInstaller> _logger;

    /// <param name="configStore">Where trust records are read and cleared.</param>
    /// <param name="pluginsDirectory">
    /// The plugins root, normally <see cref="StoragePaths.PluginsDirectory"/>. Resolved and stripped
    /// of any trailing separator, because <see cref="Remove"/> compares it against resolved paths.
    /// </param>
    /// <param name="stagingDirectory">Scratch space, normally <see cref="StoragePaths.StagingDirectory"/>.</param>
    /// <param name="isLoaded">
    /// Whether a directory's provider is running right now. Must match directory names
    /// case-insensitively, as the file system does.
    /// </param>
    /// <param name="logger">Where every write is recorded.</param>
    public PluginInstaller(
        IConfigStore configStore,
        string pluginsDirectory,
        string stagingDirectory,
        Func<string, bool> isLoaded,
        ILogger<PluginInstaller> logger)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(isLoaded);
        ArgumentNullException.ThrowIfNull(logger);

        _configStore = configStore;
        _pluginsDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginsDirectory));
        _stagingDirectory = stagingDirectory;
        _isLoaded = isLoaded;
        _logger = logger;
    }

    /// <summary>Read an archive and work out what installing it would mean.</summary>
    /// <param name="zipPath">The archive the user picked.</param>
    /// <param name="inspection">The judgement, when this returns <see langword="true"/>. The caller owns it.</param>
    /// <param name="failure">Why the archive is not a plugin package at all. Empty on success.</param>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Classify hands the package to the PluginInspection it returns, which owns and disposes it from "
            + "then on; the catch block disposes it on every other path. The rule cannot see ownership transfer through "
            + "a constructor argument two calls deep.")]
    public bool TryInspect(string zipPath, out PluginInspection? inspection, out string failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);

        inspection = null;

        Directory.CreateDirectory(_stagingDirectory);

        if (!PluginPackageReader.TryRead(zipPath, _stagingDirectory, PluginApiVersion.Host, out var package, out failure))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("A plugin package was refused: {Failure}", failure);
            }

            return false;
        }

        try
        {
            inspection = Classify(package!);
            failure = string.Empty;
            return true;
        }
        catch
        {
            package!.Dispose();
            throw;
        }
    }

    /// <summary>Put an inspected package into the plugins directory.</summary>
    /// <param name="inspection">
    /// A <see cref="PluginInstallOutcome.Fresh"/> or <see cref="PluginInstallOutcome.Upgrade"/>
    /// inspection. The package's staging folder is consumed.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The inspection was <see cref="PluginInstallOutcome.AlreadyInstalled"/> or
    /// <see cref="PluginInstallOutcome.Conflict"/>, neither of which has anything to install.
    /// </exception>
    public PluginInstallResult Install(PluginInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);

        if (inspection.Outcome is PluginInstallOutcome.AlreadyInstalled or PluginInstallOutcome.Conflict)
        {
            throw new InvalidOperationException(
                $"An inspection with outcome {inspection.Outcome} has nothing to install.");
        }

        var directory = inspection.DirectoryName;

        // Its files are open, so a delete would stop partway through the plugin the user is running.
        if (inspection.IsInstalledPluginLoaded)
        {
            return new PluginInstallResult(
                false,
                directory,
                null,
                $"{directory} is running, and its files are open. Restart the app, then add it again.");
        }

        var target = Path.Combine(_pluginsDirectory, directory);

        try
        {
            Directory.CreateDirectory(_pluginsDirectory);

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            MoveDirectory(inspection.Package.ContentPath, target);

            // Different files are a different decision, so the old approval does not carry over.
            ClearRecordedApproval(directory);

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Plugin {Directory} was installed.", directory);
            }

            return new PluginInstallResult(
                true,
                directory,
                PluginManifest.Compute(target).Sha256,
                $"{directory} was installed. Approve it to let it load on the next start.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Plugin {Directory} could not be installed.", directory);

            return new PluginInstallResult(
                false,
                directory,
                null,
                $"{directory} could not be installed: {ex.Message}");
        }
    }

    /// <summary>Delete a plugin directory and forget everything recorded about it.</summary>
    /// <param name="directoryName">
    /// A directory name the Plugins page is showing. Anything that is not the plain name of a
    /// direct child of the plugins root is refused.
    /// </param>
    /// <remarks>
    /// Success means "nothing of this name is installed or recorded any more", not "a folder was
    /// deleted": a name whose folder is already gone succeeds having deleted nothing.
    /// </remarks>
    public PluginInstallResult Remove(string directoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryName);

        if (!TryResolveChild(directoryName, out var target))
        {
            return new PluginInstallResult(
                false,
                directoryName,
                null,
                $"'{directoryName}' is not a folder in the plugins directory.");
        }

        // The same refusal, and for the same reason, as replacing one: its files are open, so a
        // delete would stop partway through the plugin the user is still running.
        if (_isLoaded(directoryName))
        {
            return new PluginInstallResult(
                false,
                directoryName,
                null,
                $"{directoryName} is running, and its files are open. Restart the app, then remove it.");
        }

        try
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            // Whether or not there was a folder to delete, so a folder removed in Explorer does not
            // leave its record behind.
            ForgetPlugin(directoryName);

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Plugin {Directory} was removed.", directoryName);
            }

            return new PluginInstallResult(
                true,
                directoryName,
                null,
                $"{directoryName} was removed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Plugin {Directory} could not be removed.", directoryName);

            return new PluginInstallResult(
                false,
                directoryName,
                null,
                $"{directoryName} could not be removed: {ex.Message}");
        }
    }

    /// <summary>Delete anything left in staging by a previous run. Called once at startup.</summary>
    public void SweepStaging()
    {
        try
        {
            if (!Directory.Exists(_stagingDirectory))
            {
                return;
            }

            foreach (var leftover in Directory.GetFileSystemEntries(_stagingDirectory))
            {
                DeleteQuietly(leftover);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The staging directory could not be swept.");
        }
    }

    /// <summary>
    /// Resolve a directory name to the path of a direct child of the plugins root, or refuse it.
    /// </summary>
    /// <remarks>
    /// Guards against deleting the wrong folder: Windows drops trailing spaces and dots, so
    /// "Doomed." resolves to Doomed and "..." resolves to the plugins root itself. The last check,
    /// that the resolved leaf is still the name asked for, is the one that catches those.
    /// </remarks>
    private bool TryResolveChild(string directoryName, out string path)
    {
        path = string.Empty;

        if (directoryName.AsSpan().ContainsAny(NotInAName))
        {
            return false;
        }

        string candidate;

        try
        {
            candidate = Path.GetFullPath(Path.Combine(_pluginsDirectory, directoryName));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        if (!string.Equals(Path.GetDirectoryName(candidate), _pluginsDirectory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(candidate), directoryName, StringComparison.Ordinal))
        {
            return false;
        }

        path = candidate;
        return true;
    }

    private void ForgetPlugin(string directory)
    {
        var config = _configStore.Load();

        var removed = config.Plugins.RemoveAll(
            plugin => string.Equals(plugin.Directory, directory, StringComparison.OrdinalIgnoreCase));

        if (removed > 0)
        {
            _configStore.Save(config);
        }
    }

    private void ClearRecordedApproval(string directory)
    {
        var config = _configStore.Load();

        var entry = config.Plugins.FirstOrDefault(
            plugin => string.Equals(plugin.Directory, directory, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            return;
        }

        entry.Enabled = false;
        entry.ManifestSha256 = null;
        entry.ApprovedUtc = null;

        _configStore.Save(config);
    }

    /// <summary>
    /// Move a directory, falling back to a copy when staging and the plugins directory are on
    /// different volumes, which <see cref="Directory.Move"/> refuses.
    /// </summary>
    private static void MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return;
        }
        catch (IOException) when (!Directory.Exists(destination))
        {
        }

        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }

        Directory.Delete(source, recursive: true);
    }

    /// <summary>Delete a leftover in staging, logging whatever stops it.</summary>
    private void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "{Path} could not be deleted.", path);
        }
    }

    private PluginInspection Classify(PluginPackage package)
    {
        var existing = Path.Combine(_pluginsDirectory, package.DirectoryName);

        if (!Directory.Exists(existing))
        {
            return new PluginInspection(
                package,
                PluginInstallOutcome.Fresh,
                $"{package.DirectoryName} will be added. You will be asked to approve it before it can load.",
                [],
                installedAssemblyName: null,
                isInstalledPluginLoaded: false);
        }

        var loaded = _isLoaded(package.DirectoryName);
        var installedAssemblyName = InstalledAssemblyNameOf(existing);

        // Identity first. The hash answers whether the bytes are the same; only the assembly name
        // answers whether the plugin is the same, so a different plugin is not silently overwritten.
        if (installedAssemblyName is null
            || !string.Equals(installedAssemblyName, package.AssemblyName, StringComparison.OrdinalIgnoreCase))
        {
            return new PluginInspection(
                package,
                PluginInstallOutcome.Conflict,
                $"The folder '{package.DirectoryName}' already holds "
                    + (installedAssemblyName is null ? "something else" : $"'{installedAssemblyName}'")
                    + $", not '{package.AssemblyName}'. Rename the zip and add it again.",
                [],
                installedAssemblyName,
                loaded);
        }

        PluginManifest installedManifest;
        try
        {
            installedManifest = PluginManifest.Compute(existing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder cannot be read, so it cannot be compared. Replacing it is exactly what a
            // user in this position wants to do.
            _logger.LogWarning(ex, "The installed plugin {Directory} could not be hashed for comparison.", package.DirectoryName);

            return new PluginInspection(
                package,
                PluginInstallOutcome.Upgrade,
                $"{package.DirectoryName} is already installed but could not be read, so what differs cannot be shown. "
                    + "Replacing it discards its approval.",
                [],
                installedAssemblyName,
                loaded);
        }

        if (installedManifest.Matches(package.Sha256))
        {
            return new PluginInspection(
                package,
                PluginInstallOutcome.AlreadyInstalled,
                $"{package.DirectoryName} is already installed, and these are the same files. Nothing was copied.",
                [],
                installedAssemblyName,
                loaded);
        }

        return new PluginInspection(
            package,
            PluginInstallOutcome.Upgrade,
            $"{package.DirectoryName} is already installed with different files. Replacing it discards its "
                + "approval, so you will be asked to approve the new files.",
            package.Manifest.DifferingFiles(installedManifest),
            installedAssemblyName,
            loaded);
    }

    /// <summary>
    /// What the installed directory's single candidate says its name is, or <see langword="null"/>
    /// if the folder does not hold exactly one candidate that can be read.
    /// </summary>
    private static string? InstalledAssemblyNameOf(string directory)
    {
        try
        {
            var candidates = Directory.GetFiles(
                directory,
                "Dongled.Plugin.*.dll",
                SearchOption.TopDirectoryOnly);

            if (candidates.Length != 1)
            {
                return null;
            }

            using var stream = new FileStream(candidates[0], FileMode.Open, FileAccess.Read, FileShare.Read);
            return PluginAssemblyIdentity.TryRead(stream, out var identity, out _) ? identity!.Name : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
