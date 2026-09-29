using System.IO;
using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Updates;

/// <summary>
/// Downloads the release asset matching the running build, verifies it against the release's
/// <c>SHA256SUMS</c>, and swaps it into the app folder.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here restarts anything. On success the new files are in place and the running process
/// is still the old version; the caller restarts it.
/// </para>
/// <para>
/// The checksum proves the zip is the one the release lists, which catches a truncated or corrupted
/// download. It does not prove who published the release: both files come from the same place.
/// </para>
/// </remarks>
public sealed class UpdateInstaller
{
    /// <summary>A zip larger than this is refused. Far above what a release is, far below what fills a disk.</summary>
    internal const long MaximumPackageBytes = 1024L * 1024 * 1024;

    /// <summary>A checksum file larger than this is refused.</summary>
    internal const int MaximumChecksumBytes = 64 * 1024;

    private readonly GitHubReleaseClient _client;
    private readonly BuildInfo _build;
    private readonly string _appDirectory;
    private readonly string _stagingDirectory;
    private readonly string _executableName;
    private readonly ILogger _logger;

    /// <param name="client">Talks to GitHub.</param>
    /// <param name="build">Decides which asset to download.</param>
    /// <param name="appDirectory">The folder the running app was started from.</param>
    /// <param name="stagingDirectory">Scratch space, normally under the per-user data folder.</param>
    /// <param name="executableName">The app's executable, which the package must contain at its root.</param>
    /// <param name="logger">Where each step is recorded.</param>
    public UpdateInstaller(
        GitHubReleaseClient client,
        BuildInfo build,
        string appDirectory,
        string stagingDirectory,
        string executableName,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _build = build;
        _appDirectory = appDirectory;
        _stagingDirectory = stagingDirectory;
        _executableName = executableName;
        _logger = logger;
    }

    /// <summary>The asset a build of <paramref name="flavor"/> is replaced by, for a release spelled <paramref name="versionText"/>.</summary>
    public static string AssetNameFor(BuildFlavor flavor, string versionText) => flavor switch
    {
        BuildFlavor.SelfContained => $"Dongled-{versionText}-win-x64.zip",
        BuildFlavor.FrameworkDependent => $"Dongled-{versionText}-win-x64-framework-dependent.zip",
        _ => throw new UpdateException("This build does not say how it was published, so Dongled cannot tell which download replaces it."),
    };

    /// <summary>Download, verify and put in place the latest release.</summary>
    /// <param name="progress">Receives the fraction of the package downloaded.</param>
    /// <param name="cancellationToken">Stops the download. Once files are being replaced it is no longer observed.</param>
    /// <returns>The release now in the app folder.</returns>
    /// <exception cref="UpdateException">Any step failed. The app folder is as it was.</exception>
    public async Task<ReleaseInfo> InstallAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!_build.CanUpdate)
        {
            throw new UpdateException("This build of Dongled does not update itself.");
        }

        // First, so a folder that cannot be written is reported before anything is downloaded.
        AppFolderSwap.EnsureWritable(_appDirectory);

        var release = await _client.GetLatestAsync(cancellationToken);

        if (release.Version <= _build.Version)
        {
            throw new UpdateException($"Dongled {_build.DisplayVersion} is already the latest release.");
        }

        var packageName = AssetNameFor(_build.Flavor, release.VersionText);
        var package = release.FindAsset(packageName)
            ?? throw new UpdateException($"Release {release.VersionText} has no {packageName}, so there is nothing to install for this build.");
        var sums = release.FindAsset(Sha256Sums.FileName)
            ?? throw new UpdateException($"Release {release.VersionText} has no {Sha256Sums.FileName}, so its download cannot be checked and was not installed.");

        var expected = Sha256Sums.Find(
            await _client.DownloadTextAsync(sums, MaximumChecksumBytes, cancellationToken),
            packageName)
            ?? throw new UpdateException($"{Sha256Sums.FileName} does not list {packageName}, so the download cannot be checked and was not installed.");

        var work = Path.Combine(_stagingDirectory, "update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            var zip = Path.Combine(work, packageName);
            var actual = await _client.DownloadFileAsync(package, zip, MaximumPackageBytes, progress, cancellationToken);

            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Refused {Package}: its SHA-256 is {Actual} but {Sums} lists {Expected}.",
                    packageName,
                    actual,
                    Sha256Sums.FileName,
                    expected);

                throw new UpdateException(
                    $"The downloaded {packageName} does not match the checksum the release lists, so it was not installed. The download may have been corrupted; try again later.");
            }

            var content = Path.Combine(work, "content");
            Extract(zip, content);

            if (!File.Exists(Path.Combine(content, _executableName)))
            {
                throw new UpdateException($"The downloaded {packageName} does not contain {_executableName} where it should, so it was not installed.");
            }

            var placed = AppFolderSwap.Apply(content, _appDirectory, _logger);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Dongled {Version} was put in place ({Count} files); it takes over when the app restarts.",
                    release.VersionText,
                    placed);
            }

            return release;
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception ex)
            {
                // Swept at the next start with the rest of staging.
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "The update's staging folder {Path} could not be removed yet.", work);
                }
            }
        }
    }

    private static void Extract(string zip, string destination)
    {
        try
        {
            // Refuses an entry whose path would land outside the destination.
            ZipFile.ExtractToDirectory(zip, destination, overwriteFiles: false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new UpdateException($"The downloaded package could not be unpacked, so it was not installed. {ex.Message}", ex);
        }
    }
}
