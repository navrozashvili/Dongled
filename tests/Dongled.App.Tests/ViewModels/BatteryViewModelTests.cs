using Dongled.Abstractions;
using Dongled.App.Tests.Fakes;
using Dongled.App.ViewModels;
using Dongled.Core.Configuration;
using Dongled.Core.Pipeline;
using Xunit;

namespace Dongled.App.Tests.ViewModels;

public class BatteryViewModelTests
{
    private readonly FakeSwitchingEngine _engine = new();
    private readonly FakeConfigStore _store = new();
    private readonly ManualUiDispatcher _dispatcher = new();
    private int _trayNotifications;

    public BatteryViewModelTests()
    {
        _engine.Sources.Add(Battery("a", "Headset"));
        _engine.Sources.Add(Battery("b", "Mouse"));
    }

    private static SourceState Battery(string id, string name) =>
        new(new AudioSourceDescriptor(id, name, null), Presence.Present, new BatteryReading(50, ChargeState.Discharging));

    private BatteryViewModel Create() => new(_engine, _store, _dispatcher, () => _trayNotifications++);

    [Fact]
    public void Moving_a_row_saves_the_order_and_redraws_the_tray()
    {
        using var viewModel = Create();
        viewModel.SelectedRow = viewModel.Rows.Single(row => row.SourceId == "b");

        viewModel.MoveUpCommand.Execute(null);

        Assert.Equal(["b", "a"], viewModel.Rows.Select(row => row.SourceId));
        Assert.Equal(["b", "a"], _store.Stored.App.BatteryDisplayOrder);
        Assert.Equal(1, _trayNotifications);
        Assert.Equal("b", viewModel.SelectedRow?.SourceId);
    }

    [Fact]
    public void An_order_that_could_not_be_saved_is_reported_and_the_tray_is_left_alone()
    {
        using var viewModel = Create();
        viewModel.SelectedRow = viewModel.Rows[1];
        _store.SaveFailure = new IOException("read-only");

        viewModel.MoveUpCommand.Execute(null);

        Assert.Equal("The battery order could not be saved: read-only", viewModel.Problem);
        Assert.Equal(0, _trayNotifications);
    }

    [Fact]
    public void The_list_follows_the_engine_on_each_poll()
    {
        using var viewModel = Create();
        _engine.Sources.Add(Battery("c", "Keyboard"));

        _dispatcher.ElapseAll();

        Assert.Equal(3, viewModel.Rows.Count);
    }

    [Fact]
    public void Disposing_stops_the_poll()
    {
        var viewModel = Create();

        viewModel.Dispose();

        Assert.All(_dispatcher.Timers, timer => Assert.True(timer.IsDisposed));
    }
}
