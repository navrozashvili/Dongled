using Dongled.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Updates;

/// <summary>Where the last update check left things.</summary>
public enum UpdateCheckStatus
{
    /// <summary>No check has finished yet.</summary>
    NeverChecked = 0,

    /// <summary>A check is in flight.</summary>
    Checking = 1,

    /// <summary>The running build is the latest release, or newer.</summary>
    UpToDate = 2,

    /// <summary>A newer release is available.</summary>
    UpdateAvailable = 3,

    /// <summary>A check the user asked for failed; <see cref="UpdateSnapshot.Problem"/> says why.</summary>
    Failed = 4,
}

/// <summary>What the pages show about updates, captured at one moment.</summary>
/// <param name="Status">Where the last check left things.</param>
/// <param name="LastCheckedUtc">When a check last finished, or null if none has.</param>
/// <param name="AvailableVersion">A newer release's version, or null if there is none.</param>
/// <param name="ReleaseNotesUrl">That release's page, or null.</param>
/// <param name="IsDismissed">Whether the user closed the banner for <paramref name="AvailableVersion"/>.</param>
/// <param name="IsInstalling">Whether an update is being downloaded or put in place.</param>
/// <param name="Problem">Why a check the user asked for failed, or null.</param>
public sealed record UpdateSnapshot(
    UpdateCheckStatus Status,
    DateTimeOffset? LastCheckedUtc,
    string? AvailableVersion,
    Uri? ReleaseNotesUrl,
    bool IsDismissed,
    bool IsInstalling,
    string? Problem)
{
    /// <summary>Nothing known.</summary>
    public static UpdateSnapshot Empty { get; } = new(UpdateCheckStatus.NeverChecked, null, null, null, false, false, null);
}

/// <summary>Update checking and installing, as the pages see it.</summary>
public interface IUpdateService
{
    /// <summary>The running build.</summary>
    BuildInfo Build { get; }

    /// <summary>Whether this build checks for and installs updates at all. False for every build from source.</summary>
    bool IsSupported { get; }

    /// <summary>The current state.</summary>
    UpdateSnapshot Current { get; }

    /// <summary>Raised when <see cref="Current"/> changes, on whichever thread changed it.</summary>
    event EventHandler? Changed;

    /// <summary>The window was opened: check in the background if the setting allows and the last check is old enough.</summary>
    /// <remarks>Returns at once. Never throws, and never reports a failure beyond a log line.</remarks>
    void NotifyWindowOpened();

    /// <summary>Check if the setting allows and the last check is old enough.</summary>
    Task CheckIfDueAsync(CancellationToken cancellationToken = default);

    /// <summary>Check now, regardless of the setting and of when the last check was. A failure is reported in <see cref="Current"/>.</summary>
    Task CheckNowAsync(CancellationToken cancellationToken = default);

    /// <summary>Stop showing the banner for the version currently offered.</summary>
    void Dismiss();

    /// <summary>Download, verify and put in place the latest release. The caller restarts the app afterwards.</summary>
    /// <returns>The version put in place.</returns>
    /// <exception cref="UpdateException">A step failed; the app folder is as it was.</exception>
    Task<string> InstallAsync(IProgress<double>? progress, CancellationToken cancellationToken = default);
}

/// <summary>
/// Decides when to ask GitHub about a new release, remembers the answer, and runs the install.
/// </summary>
/// <remarks>
/// <para>
/// The only trigger is the user opening the window. There is no timer: a tray app that works
/// should not make itself noticed, and one that is never opened never makes a request.
/// </para>
/// <para>
/// A check is skipped if the last one finished within <see cref="SuccessInterval"/>, or within
/// <see cref="FailureInterval"/> if it failed, so opening and closing the window repeatedly asks
/// GitHub at most a few times a day and never comes near its unauthenticated limit.
/// </para>
/// </remarks>
public sealed class UpdateService : IUpdateService
{
    /// <summary>How long a successful check is trusted.</summary>
    public static readonly TimeSpan SuccessInterval = TimeSpan.FromHours(6);

    /// <summary>How long after a failed check the next automatic one waits.</summary>
    public static readonly TimeSpan FailureInterval = TimeSpan.FromHours(1);

    private readonly GitHubReleaseClient _client;
    private readonly UpdateInstaller _installer;
    private readonly IConfigStore _configStore;
    private readonly IUpdateStateStore _stateStore;
    private readonly TimeProvider _time;
    private readonly ILogger<UpdateService> _logger;
    private readonly Lock _gate = new();

    private UpdateSnapshot _current;
    private bool _checking;
    private bool _installing;

    /// <param name="build">The running build.</param>
    /// <param name="client">Talks to GitHub.</param>
    /// <param name="installer">Puts a release in place.</param>
    /// <param name="configStore">For the setting.</param>
    /// <param name="stateStore">Where the last result is kept.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">Where failures go.</param>
    public UpdateService(
        BuildInfo build,
        GitHubReleaseClient client,
        UpdateInstaller installer,
        IConfigStore configStore,
        IUpdateStateStore stateStore,
        TimeProvider time,
        ILogger<UpdateService> logger)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        Build = build;
        _client = client;
        _installer = installer;
        _configStore = configStore;
        _stateStore = stateStore;
        _time = time;
        _logger = logger;

        // A development build never reads or shows anything about updates.
        _current = build.CanUpdate ? FromStored(stateStore.LoadUpdateState()) : UpdateSnapshot.Empty;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public BuildInfo Build { get; }

    /// <inheritdoc />
    public bool IsSupported => Build.CanUpdate;

    /// <inheritdoc />
    public UpdateSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc />
    public void NotifyWindowOpened()
    {
        if (!IsSupported)
        {
            return;
        }

        // Off the UI thread, so the window is on screen before anything touches the network.
        _ = Task.Run(() => CheckIfDueAsync());
    }

    /// <inheritdoc />
    public async Task CheckIfDueAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsSupported || !_configStore.Load().App.CheckForUpdates)
            {
                return;
            }

            var stored = _stateStore.LoadUpdateState();
            if (stored.LastCheckedUtc is { } last)
            {
                var elapsed = _time.GetUtcNow() - last;
                var interval = stored.LastCheckSucceeded ? SuccessInterval : FailureInterval;

                // A last check in the future means the clock moved back; checking is the safe reading.
                if (elapsed >= TimeSpan.Zero && elapsed < interval)
                {
                    return;
                }
            }

            await CheckAsync(manual: false, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(ex, "The automatic update check did not complete.");
        }
    }

    /// <inheritdoc />
    public Task CheckNowAsync(CancellationToken cancellationToken = default) =>
        IsSupported ? CheckAsync(manual: true, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public void Dismiss()
    {
        string? version;
        lock (_gate)
        {
            version = _current.AvailableVersion;
            if (version is null || _current.IsDismissed)
            {
                return;
            }

            _current = _current with { IsDismissed = true };
        }

        TrySave(state => state.DismissedVersion = version);
        RaiseChanged();
    }

    /// <inheritdoc />
    public async Task<string> InstallAsync(IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_installing)
            {
                throw new UpdateException("An update is already being installed.");
            }

            _installing = true;
            _current = _current with { IsInstalling = true };
        }

        RaiseChanged();

        try
        {
            var release = await Task.Run(() => _installer.InstallAsync(progress, cancellationToken), cancellationToken);
            return release.VersionText;
        }
        finally
        {
            lock (_gate)
            {
                _installing = false;
                _current = _current with { IsInstalling = false };
            }

            RaiseChanged();
        }
    }

    private static bool IsNewer(string? candidate, Version? current) =>
        current is not null
        && ReleaseVersion.TryParse(candidate, out var version)
        && version > current;

    private UpdateSnapshot FromStored(UpdateCheckState stored)
    {
        var available = IsNewer(stored.LatestVersion, Build.Version) ? stored.LatestVersion : null;

        var status = stored.LastCheckedUtc is null
            ? UpdateCheckStatus.NeverChecked
            : available is not null
                ? UpdateCheckStatus.UpdateAvailable
                : UpdateCheckStatus.UpToDate;

        return new UpdateSnapshot(
            status,
            stored.LastCheckedUtc,
            available,
            available is null ? null : UpdateEndpoints.ReleasePage(available),
            available is not null && string.Equals(stored.DismissedVersion, available, StringComparison.Ordinal),
            IsInstalling: false,
            Problem: null);
    }

    private async Task CheckAsync(bool manual, CancellationToken cancellationToken)
    {
        UpdateSnapshot before;
        lock (_gate)
        {
            if (_checking)
            {
                return;
            }

            _checking = true;
            before = _current;
            _current = _current with { Status = UpdateCheckStatus.Checking, Problem = null };
        }

        RaiseChanged();

        try
        {
            ReleaseInfo release;
            try
            {
                release = await Task.Run(() => _client.GetLatestAsync(cancellationToken), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Anything else is unexpected, but a check is optional and must never take the
                // page, or the app, down with it.
                var reason = ex is UpdateException
                    ? ex.Message
                    : "Something unexpected went wrong while asking GitHub.";

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(ex, "Update check failed: {Reason}", reason);
                }

                var now = _time.GetUtcNow();
                TrySave(state =>
                {
                    state.LastCheckedUtc = now;
                    state.LastCheckSucceeded = false;
                });

                lock (_gate)
                {
                    // An automatic check fails silently: the pages keep showing what was known.
                    _current = manual
                        ? before with { Status = UpdateCheckStatus.Failed, Problem = reason }
                        : before;
                }

                return;
            }

            var checkedAt = _time.GetUtcNow();
            var stored = TrySave(state =>
            {
                state.LastCheckedUtc = checkedAt;
                state.LastCheckSucceeded = true;
                state.LatestVersion = release.VersionText;
            });

            stored ??= new UpdateCheckState
            {
                LastCheckedUtc = checkedAt,
                LastCheckSucceeded = true,
                LatestVersion = release.VersionText,
            };

            var snapshot = FromStored(stored);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Update check: latest release is {Latest}, running {Current}.",
                    release.VersionText,
                    Build.DisplayVersion);
            }

            if (snapshot.AvailableVersion is { } newer && _logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Dongled {Version} is available.", newer);
            }

            lock (_gate)
            {
                _current = snapshot with { IsInstalling = _installing };
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                _current = before;
            }

            throw;
        }
        finally
        {
            lock (_gate)
            {
                _checking = false;
            }

            RaiseChanged();
        }
    }

    /// <summary>Change the stored state; a failure to write is logged and otherwise ignored.</summary>
    /// <returns>The state as saved, or null if it could not be saved.</returns>
    private UpdateCheckState? TrySave(Action<UpdateCheckState> change)
    {
        try
        {
            var state = _stateStore.LoadUpdateState();
            change(state);
            _stateStore.SaveUpdateState(state);
            return state;
        }
        catch (Exception ex)
        {
            // What a check found is a convenience. Losing it costs one extra request later.
            _logger.LogInformation(ex, "The update check result could not be saved.");
            return null;
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
