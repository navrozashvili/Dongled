using Dongled.App.Presentation;
using Dongled.App.Services;
using Dongled.App.Tests.Fakes;
using Dongled.App.ViewModels;
using Dongled.Core.Plugins;
using Xunit;

namespace Dongled.App.Tests.ViewModels;

public class PluginInstallFlowTests
{
    private readonly FakePluginInstaller _installer = new();
    private readonly PluginSession _session = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly RecordingHost _host = new();

    private PluginInstallFlow Create() => new(_dialogs, _installer, _session, _host);

    [Fact]
    public async Task Cancelling_the_file_picker_does_nothing()
    {
        await Create().RunAsync();

        Assert.Empty(_installer.Inspected);
        Assert.Empty(_dialogs.Confirmations);
        Assert.Empty(_host.Calls);
    }

    [Fact]
    public async Task A_file_that_is_not_a_package_is_refused_with_the_reason()
    {
        _dialogs.PickedFile = @"C:\Downloads\notes.zip";

        await Create().RunAsync();

        Assert.Equal([("That is not a plugin package", "The archive has no plugin assembly.")], _dialogs.Messages);
        Assert.Empty(_installer.Installed);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_at_all_is_refused_rather_than_thrown()
    {
        _dialogs.PickedFile = @"C:\Downloads\broken.zip";
        _installer.InspectionThrows = new InvalidDataException("End of central directory not found.");

        await Create().RunAsync();

        Assert.Equal(
            [("That is not a plugin package", "This file could not be read: End of central directory not found.")],
            _dialogs.Messages);
    }

    [Theory]
    [InlineData(PluginInstallOutcome.Conflict)]
    [InlineData(PluginInstallOutcome.AlreadyInstalled)]
    public async Task A_package_with_nothing_to_install_is_stopped_before_installing(PluginInstallOutcome outcome)
    {
        _dialogs.PickedFile = @"C:\Downloads\sample.zip";
        _installer.Inspection = new FakeInspection { Outcome = outcome, Message = "Nothing to do." };

        await Create().RunAsync();

        Assert.Equal([("Cannot add Sample", "Nothing to do.")], _dialogs.Messages);
        Assert.Empty(_installer.Installed);
        Assert.True(_installer.Inspection.IsDisposed);
    }

    [Fact]
    public async Task A_running_plugin_cannot_be_replaced_until_restart()
    {
        _dialogs.PickedFile = @"C:\Downloads\sample.zip";
        _installer.Inspection = new FakeInspection { Outcome = PluginInstallOutcome.Upgrade, IsInstalledPluginLoaded = true };

        await Create().RunAsync();

        Assert.Equal("Cannot replace Sample yet", Assert.Single(_dialogs.Messages).Title);
        Assert.Empty(_installer.Installed);
    }

    [Fact]
    public async Task Replacing_a_plugin_lists_the_files_that_differ()
    {
        _dialogs.PickedFile = @"C:\Downloads\sample.zip";
        _installer.Inspection = new FakeInspection
        {
            Outcome = PluginInstallOutcome.Upgrade,
            DifferingFiles = ["Dongled.Plugin.Sample.dll", "Helper.dll"],
        };
        _dialogs.Answer(false);

        await Create().RunAsync();

        var request = Assert.Single(_dialogs.Confirmations);
        Assert.Equal("Replace Sample?", request.Title);
        Assert.Equal("Replace", request.ConfirmButton);
        Assert.Contains(request.Fields, field => field.Monospace && field.Value.Contains("Helper.dll", StringComparison.Ordinal));
        Assert.Empty(_installer.Installed);
    }

    [Fact]
    public async Task A_failed_install_is_reported_and_approval_is_not_asked_for()
    {
        _dialogs.PickedFile = @"C:\Downloads\sample.zip";
        _installer.Inspection = new FakeInspection();
        _installer.InstallResult = inspection => new PluginInstallResult(false, inspection.DirectoryName, null, "The copy failed.");
        _dialogs.Answer(true);

        await Create().RunAsync();

        Assert.Single(_dialogs.Confirmations);
        Assert.Equal(["Problem: The copy failed."], _host.Calls);
        Assert.Empty(_session.Installed);
    }

    [Fact]
    public async Task A_successful_install_is_announced_before_approval_is_asked_for()
    {
        _dialogs.PickedFile = @"C:\Downloads\sample.zip";
        _installer.Inspection = new FakeInspection();
        _dialogs.Answer(true).Answer(true);

        await Create().RunAsync();

        Assert.Equal(
            ["ConfigurationChanged", "Notice: Sample was installed.", "Refresh", "Approve: Sample LANDED Sample"],
            _host.Calls);
        Assert.Equal("LANDED", _session.InstalledSha256Of("Sample"));
        Assert.True(_installer.Inspection.IsDisposed);
    }

    [Fact]
    public async Task Answering_not_now_installs_without_approving()
    {
        _dialogs.PickedFile = @"C:\Downloads\sample.zip";
        _installer.Inspection = new FakeInspection();
        _dialogs.Answer(true).Answer(false);

        await Create().RunAsync();

        Assert.Single(_installer.Installed);
        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("Approve", StringComparison.Ordinal));
    }

    /// <summary>Records what the flow asked of its page, in order.</summary>
    private sealed class RecordingHost : IPluginInstallFlowHost
    {
        public List<string> Calls { get; } = [];

        public void ReportProblem(string problem) => Calls.Add($"Problem: {problem}");

        public void ReportNotice(string notice) => Calls.Add($"Notice: {notice}");

        public void NotifyConfigurationChanged() => Calls.Add("ConfigurationChanged");

        public void Refresh() => Calls.Add("Refresh");

        public void ApproveInstalled(string directory, string sha256, string title) =>
            Calls.Add($"Approve: {directory} {sha256} {title}");
    }
}
