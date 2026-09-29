using Dongled.Abstractions;
using Dongled.App.Presentation;
using Dongled.App.Services;
using Dongled.App.Tests.Fakes;
using Dongled.App.ViewModels;
using Dongled.Core.Configuration;
using Dongled.Core.Pipeline;
using Dongled.Core.Plugins;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Dongled.App.Tests.ViewModels;

public class PluginsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly FakePluginRuntime _runtime = new();
    private readonly FakeConfigStore _store = new();
    private readonly FakeSwitchingEngine _engine = new();
    private readonly FakePluginInstaller _installer = new();
    private readonly PluginSession _session = new();
    private readonly ManualUiDispatcher _dispatcher = new();
    private readonly FakeDialogService _dialogs = new();
    private int _restarts;

    public PluginsViewModelTests() => _runtime.StartedSource.SetResult();

    private PluginsViewModel Create() => new(
        _runtime,
        _store,
        _engine,
        new FixedTimeProvider(Now),
        _installer,
        _session,
        _dispatcher,
        _dialogs,
        () => _restarts++);

    private static PluginListing Listing(string directory, PluginLoadStatus status, string? sha256 = "HASH", string? displayName = null) =>
        new(
            directory,
            status,
            $"{directory} is {status}.",
            sha256,
            displayName is null ? null : new ProviderMetadata(directory, displayName, "Does things.", IsExperimental: false));

    private PluginConfig? Stored(string directory) =>
        _store.Stored.Plugins.SingleOrDefault(plugin => plugin.Directory == directory);

    [Fact]
    public void Each_directory_is_listed_with_a_status_and_the_actions_it_calls_for()
    {
        _runtime.Results.Add(Listing("Running", PluginLoadStatus.Loaded, displayName: "Running Plugin"));
        _runtime.States["Running"] = ProviderRunState.Running;
        _runtime.Results.Add(Listing("New", PluginLoadStatus.Unapproved));
        _runtime.Results.Add(Listing("Junk", PluginLoadStatus.NotAPlugin, sha256: null));
        _store.Save(new AppConfig { Plugins = [new PluginConfig { Directory = "Running", Enabled = true }] });

        var viewModel = Create();

        Assert.False(viewModel.IsLoading);
        Assert.Equal(["Running Plugin", "New", "Junk"], viewModel.Rows.Select(row => row.Title));
        Assert.Equal(["Running", "Blocked — new", "Not a plugin"], viewModel.Rows.Select(row => row.Status));
        Assert.Equal([false, true, false], viewModel.Rows.Select(row => row.CanApprove));
        Assert.Equal([true, false, false], viewModel.Rows.Select(row => row.CanDisable));
        Assert.All(viewModel.Rows, row => Assert.True(row.CanRemove));
    }

    [Fact]
    public void Nothing_is_listed_until_loading_has_finished()
    {
        var runtime = new FakePluginRuntime();
        runtime.Results.Add(Listing("Sample", PluginLoadStatus.Unapproved));

        var viewModel = new PluginsViewModel(
            runtime, _store, _engine, TimeProvider.System, _installer, _session, _dispatcher, _dialogs, () => { });

        Assert.True(viewModel.IsLoading);
        Assert.False(viewModel.HasNoRows);
        Assert.Empty(viewModel.Rows);

        runtime.StartedSource.SetResult();

        Assert.False(viewModel.IsLoading);
        Assert.Single(viewModel.Rows);
    }

    [Fact]
    public async Task Approving_asks_first_and_shows_the_hash_it_will_record()
    {
        _runtime.Results.Add(Listing("Sample", PluginLoadStatus.Unapproved, sha256: "ABC123"));
        var viewModel = Create();
        _dialogs.Answer(true);

        await viewModel.ApproveAsync(viewModel.Rows[0]);

        var request = Assert.Single(_dialogs.Confirmations);
        Assert.Equal("Enable Sample?", request.Title);
        Assert.Equal(PluginPrompts.ApprovalWarning, request.Message);
        Assert.Equal("Cancel", request.CancelButton);
        Assert.Contains(request.Fields, field => field.Value == "ABC123" && field.Monospace);

        var entry = Stored("Sample");
        Assert.NotNull(entry);
        Assert.True(entry.Enabled);
        Assert.Equal("ABC123", entry.ManifestSha256);
        Assert.Equal(Now, entry.ApprovedUtc);

        Assert.True(viewModel.NeedsRestart);
        Assert.StartsWith("Sample is approved.", viewModel.Notice, StringComparison.Ordinal);
        Assert.Equal(1, _engine.ReloadRequests);
        Assert.Equal(1, _runtime.ReloadRequests);
    }

    [Fact]
    public async Task Declining_approval_changes_nothing()
    {
        _runtime.Results.Add(Listing("Sample", PluginLoadStatus.Unapproved));
        var viewModel = Create();
        _dialogs.Answer(false);

        await viewModel.ApproveAsync(viewModel.Rows[0]);

        Assert.Equal(0, _store.SaveCount);
        Assert.False(viewModel.NeedsRestart);
        Assert.False(viewModel.HasNotice);
    }

    [Fact]
    public async Task A_folder_that_could_not_be_hashed_cannot_be_approved()
    {
        _runtime.Results.Add(Listing("Sample", PluginLoadStatus.Unapproved, sha256: null));
        var viewModel = Create();
        _dialogs.Answer(false);

        await viewModel.ApproveAsync(viewModel.Rows[0]);

        var request = Assert.Single(_dialogs.Confirmations);
        Assert.False(request.CanConfirm);
        Assert.Contains(request.Fields, field => field.Value.Contains("could not be hashed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Switching_off_keeps_the_recorded_approval()
    {
        _runtime.Results.Add(Listing("Sample", PluginLoadStatus.Loaded));
        _store.Save(new AppConfig
        {
            Plugins = [new PluginConfig { Directory = "Sample", Enabled = true, ManifestSha256 = "HASH" }],
        });
        var viewModel = Create();
        _dialogs.Answer(true);

        await viewModel.DisableAsync(viewModel.Rows[0]);

        var entry = Stored("Sample");
        Assert.NotNull(entry);
        Assert.False(entry.Enabled);
        Assert.Equal("HASH", entry.ManifestSha256);
        Assert.True(viewModel.NeedsRestart);
    }

    [Fact]
    public async Task Removing_deletes_the_folder_and_drops_the_row()
    {
        _runtime.Results.Add(Listing("Sample", PluginLoadStatus.Unapproved));
        _runtime.Results.Add(Listing("Other", PluginLoadStatus.Unapproved));
        var viewModel = Create();
        _dialogs.Answer(true);

        await viewModel.RemoveAsync(viewModel.Rows[0]);

        Assert.Equal(["Sample"], _installer.Removed);
        Assert.Equal(["Other"], viewModel.Rows.Select(row => row.Directory));
        Assert.Equal(["Sample"], _session.Removed);
        Assert.Equal("Sample was removed.", viewModel.Notice);
    }

    [Fact]
    public async Task A_removal_that_fails_says_why_and_keeps_the_row()
    {
        _runtime.Results.Add(Listing("Sample", PluginLoadStatus.Unapproved));
        _installer.RemoveResult = directory => new PluginInstallResult(false, directory, null, "Sample is in use.");
        var viewModel = Create();
        _dialogs.Answer(true);

        await viewModel.RemoveAsync(viewModel.Rows[0]);

        Assert.Equal("Sample is in use.", viewModel.Problem);
        Assert.Single(viewModel.Rows);
        Assert.Empty(_session.Removed);
    }

    [Fact]
    public async Task Adding_a_package_installs_it_and_then_asks_for_approval_of_the_installed_hash()
    {
        _dialogs.PickedFile = @"C:\Downloads\sample.zip";
        _installer.Inspection = new FakeInspection();
        var viewModel = Create();
        _dialogs.Answer(true).Answer(true);

        await viewModel.AddPluginAsync();

        Assert.Equal(2, _dialogs.Confirmations.Count);
        Assert.Equal("Add Sample?", _dialogs.Confirmations[0].Title);

        var approval = _dialogs.Confirmations[1];
        Assert.Equal("Enable Sample?", approval.Title);
        Assert.Equal("Not now", approval.CancelButton);
        Assert.Contains(approval.Fields, field => field.Value == "LANDED");

        var entry = Stored("Sample");
        Assert.NotNull(entry);
        Assert.True(entry.Enabled);
        Assert.Equal("LANDED", entry.ManifestSha256);

        var row = Assert.Single(viewModel.Rows);
        Assert.Equal("Installed — loads on next start", row.Status);
        Assert.True(viewModel.NeedsRestart);
        Assert.True(_installer.Inspection.IsDisposed);
    }

    [Fact]
    public async Task An_installed_plugin_left_unapproved_is_listed_as_blocked_and_says_what_happened()
    {
        _dialogs.PickedFile = @"C:\Downloads\sample.zip";
        _installer.Inspection = new FakeInspection();
        var viewModel = Create();
        _dialogs.Answer(true).Answer(false);

        await viewModel.AddPluginAsync();

        var row = Assert.Single(viewModel.Rows);
        Assert.Equal("Blocked — new", row.Status);
        Assert.True(row.CanApprove);
        Assert.Equal("Sample was installed.", viewModel.Notice);
        Assert.False(viewModel.NeedsRestart);
    }

    [Fact]
    public async Task A_failed_install_is_reported_and_approval_is_not_asked_for()
    {
        _dialogs.PickedFile = @"C:\Downloads\sample.zip";
        _installer.Inspection = new FakeInspection();
        _installer.InstallResult = inspection => new PluginInstallResult(false, inspection.DirectoryName, null, "The copy failed.");
        var viewModel = Create();
        _dialogs.Answer(true);

        await viewModel.AddPluginAsync();

        Assert.Single(_dialogs.Confirmations);
        Assert.Equal("The copy failed.", viewModel.Problem);
        Assert.Empty(viewModel.Rows);
    }

    [Fact]
    public async Task Only_one_dialog_flow_runs_at_a_time()
    {
        _runtime.Results.Add(Listing("Sample", PluginLoadStatus.Unapproved));
        var viewModel = Create();
        var answer = new TaskCompletionSource<bool>();
        _dialogs.AnswerLater(answer.Task);

        var first = viewModel.ApproveAsync(viewModel.Rows[0]);
        await viewModel.RemoveAsync(viewModel.Rows[0]);

        Assert.Single(_dialogs.Confirmations);

        answer.SetResult(false);
        await first;
    }

    [Fact]
    public async Task Adding_a_plugin_waits_its_turn_behind_another_dialog_flow()
    {
        _runtime.Results.Add(Listing("Sample", PluginLoadStatus.Unapproved));
        _dialogs.PickedFile = @"C:\Downloads\sample.zip";
        _installer.Inspection = new FakeInspection();
        var viewModel = Create();
        var answer = new TaskCompletionSource<bool>();
        _dialogs.AnswerLater(answer.Task);

        var first = viewModel.ApproveAsync(viewModel.Rows[0]);
        await viewModel.AddPluginAsync();

        Assert.Empty(_installer.Inspected);

        answer.SetResult(false);
        await first;
    }

    [Fact]
    public async Task A_dialog_that_fails_to_open_is_reported_rather_than_thrown()
    {
        _runtime.Results.Add(Listing("Sample", PluginLoadStatus.Unapproved));
        var viewModel = Create();
        _dialogs.ConfirmFailure = new InvalidOperationException("Only a single ContentDialog can be open at any time.");

        await viewModel.RemoveAsync(viewModel.Rows[0]);

        Assert.Equal(("Sample could not be removed", "Only a single ContentDialog can be open at any time."), Assert.Single(_dialogs.Messages));
    }

    [Fact]
    public void Choosing_a_log_level_saves_it()
    {
        _runtime.Results.Add(Listing("Sample", PluginLoadStatus.Loaded));
        _store.Save(new AppConfig { Plugins = [new PluginConfig { Directory = "Sample", Enabled = true }] });
        var viewModel = Create();

        viewModel.Rows[0].LogLevelIndex = Array.IndexOf(PluginRow.Levels, LogLevel.Debug);

        Assert.Equal(LogLevel.Debug, Stored("Sample")?.LogLevel);
    }

    [Fact]
    public void A_shipped_plugin_with_no_entry_is_marked_and_can_be_switched_off()
    {
        _runtime.Results.Add(Listing("Shipped", PluginLoadStatus.Loaded) with { IsBundled = true });
        _runtime.States["Shipped"] = ProviderRunState.Running;
        _runtime.Results.Add(Listing("Other", PluginLoadStatus.Unapproved));

        var viewModel = Create();

        Assert.Equal([true, false], viewModel.Rows.Select(row => row.IsBundled));
        Assert.Equal("Running", viewModel.Rows[0].Status);
        Assert.False(viewModel.Rows[0].CanApprove);
        Assert.True(viewModel.Rows[0].CanDisable);
        Assert.True(viewModel.Rows[0].CanChooseLogLevel);
    }

    [Fact]
    public async Task Switching_a_shipped_plugin_off_records_an_entry_that_keeps_it_off()
    {
        _runtime.Results.Add(Listing("Shipped", PluginLoadStatus.Loaded) with { IsBundled = true });
        var viewModel = Create();
        _dialogs.Answer(true);

        await viewModel.DisableAsync(viewModel.Rows[0]);

        var entry = Stored("Shipped");
        Assert.NotNull(entry);
        Assert.False(entry.Enabled);
        Assert.True(viewModel.NeedsRestart);
    }

    [Fact]
    public void A_switched_off_shipped_plugin_can_be_switched_back_on()
    {
        _runtime.Results.Add(Listing("Shipped", PluginLoadStatus.Disabled) with { IsBundled = true });
        _store.Save(new AppConfig { Plugins = [new PluginConfig { Directory = "Shipped", Enabled = false }] });

        var viewModel = Create();

        Assert.True(viewModel.Rows[0].CanApprove);
        Assert.False(viewModel.Rows[0].CanDisable);
    }

    [Fact]
    public void Choosing_a_log_level_for_a_shipped_plugin_does_not_switch_it_off()
    {
        _runtime.Results.Add(Listing("Shipped", PluginLoadStatus.Loaded) with { IsBundled = true });
        var viewModel = Create();

        viewModel.Rows[0].LogLevelIndex = Array.IndexOf(PluginRow.Levels, LogLevel.Debug);

        var entry = Stored("Shipped");
        Assert.NotNull(entry);
        Assert.Equal(LogLevel.Debug, entry.LogLevel);
        Assert.True(entry.Enabled);
    }

    [Fact]
    public void Choosing_a_log_level_for_a_plugin_that_is_not_shipped_creates_no_approval()
    {
        _runtime.Results.Add(Listing("Sample", PluginLoadStatus.Unapproved));
        var viewModel = Create();

        viewModel.Rows[0].LogLevelIndex = Array.IndexOf(PluginRow.Levels, LogLevel.Debug);

        Assert.False(Stored("Sample")?.Enabled ?? false);
    }

    [Fact]
    public void Restart_is_offered_through_its_command()
    {
        var viewModel = Create();

        viewModel.RestartCommand.Execute(null);

        Assert.Equal(1, _restarts);
    }
}
