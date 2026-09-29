using System.IO;
using System.IO.Compression;

namespace Dongled.Core.Plugins;

/// <summary>
/// Turns a <c>.zip</c> the user chose into a validated <see cref="PluginPackage"/>, or into one
/// sentence saying why it is not one.
/// </summary>
/// <remarks>
/// Everything that can be judged is judged before the result is offered to the user, so a package
/// that could only ever show "Failed to start" is refused while they still have the file in hand.
/// Nothing reaches the plugins directory from here; that is the installer's job.
/// </remarks>
internal static class PluginPackageReader
{
    private const string CandidatePattern = "Dongled.Plugin.*.dll";
    private const string SdkAssemblyName = "Dongled.Abstractions";

    /// <summary>Validate and extract an archive.</summary>
    /// <param name="zipPath">The archive the user picked.</param>
    /// <param name="stagingRoot">
    /// Where a private folder for this package is created, normally
    /// <see cref="Configuration.StoragePaths.StagingDirectory"/>.
    /// </param>
    /// <param name="hostApiVersion">
    /// The SDK version this host provides. A parameter rather than
    /// <see cref="PluginApiVersion.Host"/> directly, so the incompatible case is testable against a
    /// real plugin assembly instead of needing a second build of the SDK.
    /// </param>
    /// <param name="package">The package, when this returns <see langword="true"/>. The caller owns it.</param>
    /// <param name="failure">One sentence a user can act on. Empty on success.</param>
    public static bool TryRead(
        string zipPath,
        string stagingRoot,
        Version hostApiVersion,
        out PluginPackage? package,
        out string failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentNullException.ThrowIfNull(hostApiVersion);

        package = null;

        if (!PluginPackageRules.TrySanitiseDirectoryName(Path.GetFileName(zipPath), out var directoryName, out failure))
        {
            return false;
        }

        string? staged = null;

        try
        {
            staged = Path.Combine(stagingRoot, "package-" + Guid.NewGuid().ToString("N"));

            // Refuses any entry that would land outside the destination.
            ZipFile.ExtractToDirectory(zipPath, staged);

            var content = UnwrapSingleFolder(staged);
            var candidates = Directory.GetFiles(content, CandidatePattern, SearchOption.TopDirectoryOnly);

            if (candidates.Length == 0)
            {
                failure = $"No file in this package matches {CandidatePattern}, so it is not a plugin.";
                return false;
            }

            if (candidates.Length > 1)
            {
                failure = $"{candidates.Length} files in this package match {CandidatePattern}; a plugin has exactly one.";
                return false;
            }

            var assemblyPath = candidates[0];
            var assemblyFileName = Path.GetFileName(assemblyPath);

            PluginAssemblyIdentity? identity;
            string identityFailure;
            using (var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (!PluginAssemblyIdentity.TryRead(stream, out identity, out identityFailure))
                {
                    failure = $"'{assemblyFileName}' {identityFailure}";
                    return false;
                }
            }

            if (identity!.SdkVersion is null)
            {
                failure = $"'{assemblyFileName}' does not reference {SdkAssemblyName}, so it is not built against the plugin SDK.";
                return false;
            }

            if (!PluginApiVersion.IsCompatible(identity.SdkVersion, hostApiVersion))
            {
                failure = $"Built for {SdkAssemblyName} {PluginApiVersion.Describe(identity.SdkVersion)}; this app provides "
                    + $"{PluginApiVersion.Describe(hostApiVersion)}.";
                return false;
            }

            package = new PluginPackage(directoryName, identity.Name, staged, content, PluginManifest.Compute(content));
            staged = null;

            failure = string.Empty;
            return true;
        }
        catch (InvalidDataException ex)
        {
            failure = $"This file could not be read as a zip archive: {ex.Message}";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = $"This package could not be unpacked: {ex.Message}";
            return false;
        }
        finally
        {
            // Non-null only on a failure path: success hands the folder to the package.
            if (staged is not null)
            {
                try
                {
                    if (Directory.Exists(staged))
                    {
                        Directory.Delete(staged, recursive: true);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>
    /// The one folder everything sits in, or <paramref name="extracted"/> itself if there is not
    /// exactly one.
    /// </summary>
    /// <remarks>
    /// Zipping a folder in Explorer wraps everything in it; zipping a folder's contents does not.
    /// Both are ordinary, so the wrapper is removed rather than becoming a nested directory the
    /// loader would ignore.
    /// </remarks>
    private static string UnwrapSingleFolder(string extracted)
    {
        if (Directory.EnumerateFiles(extracted).Any())
        {
            return extracted;
        }

        var folders = Directory.GetDirectories(extracted);
        return folders.Length == 1 ? folders[0] : extracted;
    }
}
