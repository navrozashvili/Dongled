using Dongled.App.Startup;
using Dongled.App.Tests.Fakes;
using Dongled.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dongled.App.Tests.Startup;

public sealed class StartupRegistrationTests
{
    private const string OtherExecutable = @"C:\Users\alice\Downloads\Dongled-1.0.2\Dongled.exe";

    private readonly FakeStartupRegistration _startup = new();
    private readonly FakeConfigStore _store = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_value_is_no_entry(string? recorded)
    {
        var entry = StartupEntry.Classify(recorded, FakeStartupRegistration.ThisExecutable, _ => true);

        Assert.Equal(StartupTarget.None, entry.Target);
        Assert.Null(entry.RecordedPath);
    }

    [Theory]
    [InlineData(@"""C:\Apps\Dongled\Dongled.exe""")]
    [InlineData(@"C:\Apps\Dongled\Dongled.exe")]
    [InlineData(@"""c:\apps\dongled\DONGLED.EXE""")]
    public void This_executable_quoted_or_not_and_in_any_case_is_this_copy(string recorded)
    {
        var entry = StartupEntry.Classify(recorded, FakeStartupRegistration.ThisExecutable, _ => false);

        Assert.Equal(StartupTarget.ThisCopy, entry.Target);
    }

    [Fact]
    public void Another_existing_executable_is_another_copy()
    {
        var entry = StartupEntry.Classify($"\"{OtherExecutable}\"", FakeStartupRegistration.ThisExecutable, _ => true);

        Assert.Equal(StartupTarget.OtherCopy, entry.Target);
        Assert.Equal(OtherExecutable, entry.RecordedPath);
    }

    [Fact]
    public void Another_executable_that_is_gone_is_a_missing_copy()
    {
        var entry = StartupEntry.Classify($"\"{OtherExecutable}\"", FakeStartupRegistration.ThisExecutable, _ => false);

        Assert.Equal(StartupTarget.MissingCopy, entry.Target);
        Assert.Equal(OtherExecutable, entry.RecordedPath);
    }

    [Fact]
    public void An_official_build_takes_over_an_entry_whose_copy_is_gone()
    {
        _startup.Recorded = $"\"{OtherExecutable}\"";

        Assert.True(StartupEntryRepair.Apply(_startup, isOfficialBuild: true, NullLogger.Instance));

        Assert.Equal(StartupTarget.ThisCopy, _startup.Read().Target);
    }

    [Fact]
    public void A_build_from_source_never_takes_over_the_entry()
    {
        _startup.Recorded = $"\"{OtherExecutable}\"";

        Assert.False(StartupEntryRepair.Apply(_startup, isOfficialBuild: false, NullLogger.Instance));

        Assert.Equal(0, _startup.Writes);
    }

    [Fact]
    public void An_entry_for_another_copy_that_exists_is_left_alone()
    {
        _startup.Recorded = $"\"{OtherExecutable}\"";
        _startup.ExistingFiles.Add(OtherExecutable);

        Assert.False(StartupEntryRepair.Apply(_startup, isOfficialBuild: true, NullLogger.Instance));

        Assert.Equal(0, _startup.Writes);
    }

    [Fact]
    public void No_entry_is_not_created_by_the_repair()
    {
        Assert.False(StartupEntryRepair.Apply(_startup, isOfficialBuild: true, NullLogger.Instance));

        Assert.Null(_startup.Recorded);
    }

    [Fact]
    public void A_failed_repair_does_not_throw()
    {
        _startup.Recorded = $"\"{OtherExecutable}\"";
        _startup.FailWrites = true;

        Assert.False(StartupEntryRepair.Apply(_startup, isOfficialBuild: true, NullLogger.Instance));
    }

    [Fact]
    public void Settings_shows_another_copy_and_can_take_it_over()
    {
        _startup.Recorded = $"\"{OtherExecutable}\"";
        _startup.ExistingFiles.Add(OtherExecutable);
        using var settings = CreateSettings();

        Assert.False(settings.StartWithWindows);
        Assert.True(settings.StartsAnotherCopy);
        Assert.Equal(OtherExecutable, settings.OtherCopyPath);
        Assert.Contains("different copy", settings.OtherCopyNotice, StringComparison.Ordinal);

        settings.UseThisCopyCommand.Execute(null);

        Assert.True(settings.StartWithWindows);
        Assert.False(settings.StartsAnotherCopy);
        Assert.Equal(StartupTarget.ThisCopy, _startup.Read().Target);
    }

    [Fact]
    public void Settings_says_when_the_registered_copy_is_gone()
    {
        _startup.Recorded = $"\"{OtherExecutable}\"";
        using var settings = CreateSettings();

        Assert.True(settings.StartsAnotherCopy);
        Assert.Contains("no longer exists", settings.OtherCopyNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void Turning_the_toggle_on_over_another_copy_takes_it_over()
    {
        _startup.Recorded = $"\"{OtherExecutable}\"";
        _startup.ExistingFiles.Add(OtherExecutable);
        using var settings = CreateSettings();

        settings.StartWithWindows = true;

        Assert.Equal(StartupTarget.ThisCopy, _startup.Read().Target);
        Assert.False(settings.StartsAnotherCopy);
    }

    [Fact]
    public void Turning_the_toggle_off_removes_the_entry_and_is_saved()
    {
        _startup.Recorded = $"\"{FakeStartupRegistration.ThisExecutable}\"";
        using var settings = CreateSettings();
        Assert.True(settings.StartWithWindows);

        settings.StartWithWindows = false;

        Assert.Null(_startup.Recorded);
        Assert.False(_store.Stored.App.StartWithWindows);
    }

    [Fact]
    public void A_failed_write_leaves_the_toggle_showing_the_registry()
    {
        using var settings = CreateSettings();
        _startup.FailWrites = true;

        settings.StartWithWindows = true;

        Assert.False(settings.StartWithWindows);
        Assert.True(settings.HasProblem);
    }

    private SettingsViewModel CreateSettings()
    {
        var updates = new FakeUpdateService();
        var dispatcher = new ManualUiDispatcher();
        return new SettingsViewModel(
            _store,
            new FakeSwitchingEngine(),
            new FakePluginRuntime(),
            _ => { },
            updates,
            new UpdateFlow(updates, new FakeDialogService(), () => { }),
            dispatcher,
            _startup);
    }
}
