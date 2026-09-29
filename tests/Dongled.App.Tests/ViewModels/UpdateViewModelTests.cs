using Dongled.App.Tests.Fakes;
using Dongled.App.ViewModels;
using Dongled.Core.Configuration;
using Dongled.Core.Logging;
using Dongled.Core.Updates;
using Xunit;

namespace Dongled.App.Tests.ViewModels;

public sealed class UpdateViewModelTests
{
    private readonly FakeUpdateService _updates = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeConfigStore _store = new();
    private readonly ManualUiDispatcher _dispatcher = new();
    private int _restarts;

    private UpdateFlow Flow() => new(_updates, _dialogs, () => _restarts++);

    private StatusViewModel CreateStatus(FakeUpdateService? updates = null) =>
        new(new FakeAudioEndpointService(), new FakeSwitchingEngine(), _store, new LogRingBuffer(), _dispatcher, updates ?? _updates, Flow());

    private SettingsViewModel CreateSettings(FakeUpdateService? updates = null) =>
        new(_store, new FakeSwitchingEngine(), new FakePluginRuntime(), _ => { }, updates ?? _updates, Flow(), _dispatcher, new FakeStartupRegistration());

    [Fact]
    public void The_banner_shows_a_newer_release()
    {
        using var status = CreateStatus();

        _updates.Offer("1.0.57");

        Assert.True(status.IsUpdateBannerOpen);
        Assert.Equal("Dongled 1.0.57 is available", status.UpdateBannerTitle);
        Assert.Equal(new Uri("https://github.com/navrozashvili/Dongled/releases/tag/v1.0.57"), status.ReleaseNotesUrl);
        Assert.True(status.UpdateCommand.CanExecute(null));
    }

    [Fact]
    public void There_is_no_banner_when_nothing_newer_is_known()
    {
        using var status = CreateStatus();

        Assert.False(status.IsUpdateBannerOpen);
        Assert.False(status.UpdateCommand.CanExecute(null));
    }

    [Fact]
    public void Closing_the_banner_dismisses_that_release()
    {
        _updates.Offer("1.0.57");
        using var status = CreateStatus();

        status.DismissUpdateCommand.Execute(null);

        Assert.Equal(1, _updates.DismissCount);
        Assert.False(status.IsUpdateBannerOpen);
    }

    [Fact]
    public void With_the_setting_off_there_is_no_banner()
    {
        _store.Save(new AppConfig { App = { CheckForUpdates = false } });
        _updates.Offer("1.0.57");

        using var status = CreateStatus();

        Assert.False(status.IsUpdateBannerOpen);
    }

    [Fact]
    public void A_development_build_never_shows_the_banner()
    {
        var development = new FakeUpdateService(supported: false);
        development.Offer("1.0.57");

        using var status = CreateStatus(development);

        Assert.False(status.IsUpdateBannerOpen);
    }

    [Fact]
    public async Task Update_asks_first_and_does_nothing_if_the_user_declines()
    {
        _updates.Offer("1.0.57");
        using var status = CreateStatus();
        _dialogs.Answer(false);

        await status.UpdateCommand.ExecuteAsync(null);

        Assert.Equal("Update to Dongled 1.0.57?", Assert.Single(_dialogs.Confirmations).Title);
        Assert.Equal(0, _updates.InstallCount);
        Assert.Equal(0, _restarts);
    }

    [Fact]
    public async Task Update_installs_and_restarts_when_the_user_agrees()
    {
        _updates.Offer("1.0.57");
        using var status = CreateStatus();
        _dialogs.Answer(true);

        await status.UpdateCommand.ExecuteAsync(null);

        Assert.Equal(1, _updates.InstallCount);
        Assert.Equal(1, _restarts);
    }

    [Fact]
    public async Task A_refused_update_is_explained_and_does_not_restart()
    {
        _updates.Offer("1.0.57");
        _updates.InstallFailure = new UpdateException("The downloaded package does not match the checksum the release lists.");
        using var status = CreateStatus();
        _dialogs.Answer(true);

        await status.UpdateCommand.ExecuteAsync(null);

        var message = Assert.Single(_dialogs.Messages);
        Assert.Equal("The update was not installed", message.Title);
        Assert.Contains("checksum", message.Message, StringComparison.Ordinal);
        Assert.Equal(0, _restarts);
    }

    [Fact]
    public void The_settings_line_says_what_the_last_check_found()
    {
        using var settings = CreateSettings();

        Assert.True(settings.IsUpdateSupported);
        Assert.Contains("Not checked yet", settings.UpdateStatus, StringComparison.Ordinal);

        _updates.Offer("1.0.57");

        Assert.StartsWith("Dongled 1.0.57 is available.", settings.UpdateStatus, StringComparison.Ordinal);
        Assert.True(settings.CanUpdate);
        Assert.True(settings.HasReleaseNotes);

        _updates.Set(UpdateSnapshot.Empty with { Status = UpdateCheckStatus.Failed, Problem = "GitHub could not be reached." });

        Assert.Equal("Could not check for updates. GitHub could not be reached.", settings.UpdateStatus);
        Assert.False(settings.CanUpdate);
    }

    [Fact]
    public async Task Check_now_asks_the_service()
    {
        using var settings = CreateSettings();

        await settings.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal(1, _updates.CheckNowCount);
    }

    [Fact]
    public void Check_now_is_unavailable_while_a_check_runs()
    {
        using var settings = CreateSettings();

        _updates.Set(UpdateSnapshot.Empty with { Status = UpdateCheckStatus.Checking });

        Assert.False(settings.CheckNowCommand.CanExecute(null));
        Assert.Equal("Checking for updates…", settings.UpdateStatus);
    }

    [Fact]
    public void The_toggle_is_persisted()
    {
        using var settings = CreateSettings();
        Assert.True(settings.CheckForUpdates);

        settings.CheckForUpdates = false;

        Assert.False(_store.Stored.App.CheckForUpdates);
    }

    [Fact]
    public void A_development_build_hides_the_update_controls()
    {
        using var settings = CreateSettings(new FakeUpdateService(supported: false));

        Assert.False(settings.IsUpdateSupported);
        Assert.True(settings.IsDevelopmentBuild);
    }

    [Fact]
    public async Task Download_progress_reaches_the_settings_line()
    {
        _updates.Offer("1.0.57");
        _updates.ProgressToReport.Add(0.5);
        using var settings = CreateSettings();
        _dialogs.Answer(true);
        var seen = new List<string>();
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.UpdateStatus))
            {
                seen.Add(settings.UpdateStatus);
            }
        };

        await settings.UpdateCommand.ExecuteAsync(null);

        Assert.Contains(seen, line => line.StartsWith("Downloading the update", StringComparison.Ordinal) && line.Contains('5', StringComparison.Ordinal));
    }
}
