using Dongled.Core.Updates;

namespace Dongled.App.Tests.Fakes;

/// <summary>An update service whose state a test sets directly, recording what was asked of it.</summary>
internal sealed class FakeUpdateService : IUpdateService
{
    public FakeUpdateService(bool supported = true) =>
        Build = supported
            ? BuildInfo.Create("true", "self-contained", "1.0.50")
            : BuildInfo.Create("false", "self-contained", "0.0.0-dev");

    public event EventHandler? Changed;

    public BuildInfo Build { get; }

    public bool IsSupported => Build.CanUpdate;

    public UpdateSnapshot Current { get; private set; } = UpdateSnapshot.Empty;

    public int WindowOpenedCount { get; private set; }

    public int CheckNowCount { get; private set; }

    public int InstallCount { get; private set; }

    public int DismissCount { get; private set; }

    /// <summary>When set, the next install throws it.</summary>
    public Exception? InstallFailure { get; set; }

    /// <summary>Fractions reported to the install's progress.</summary>
    public List<double> ProgressToReport { get; } = [];

    public void Set(UpdateSnapshot snapshot)
    {
        Current = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Offer(string version) =>
        Set(UpdateSnapshot.Empty with
        {
            Status = UpdateCheckStatus.UpdateAvailable,
            AvailableVersion = version,
            ReleaseNotesUrl = UpdateEndpoints.ReleasePage(version),
            LastCheckedUtc = DateTimeOffset.UnixEpoch,
        });

    public void NotifyWindowOpened() => WindowOpenedCount++;

    public Task CheckIfDueAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        CheckNowCount++;
        return Task.CompletedTask;
    }

    public void Dismiss()
    {
        DismissCount++;
        Set(Current with { IsDismissed = true });
    }

    public Task<string> InstallAsync(IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        InstallCount++;
        Set(Current with { IsInstalling = true });

        foreach (var fraction in ProgressToReport)
        {
            progress?.Report(fraction);
        }

        Set(Current with { IsInstalling = false });

        if (InstallFailure is { } failure)
        {
            InstallFailure = null;
            return Task.FromException<string>(failure);
        }

        return Task.FromResult(Current.AvailableVersion ?? "none");
    }
}
