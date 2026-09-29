using System.Diagnostics.CodeAnalysis;
using Dongled.App.Tests.Fakes;
using Dongled.App.Tray;
using Dongled.App.ViewModels;
using Dongled.Core.Audio;
using Dongled.Core.Configuration;
using Xunit;

namespace Dongled.App.Tests.ViewModels;

public class ShellViewModelTests
{
    private readonly FakeAudioEndpointService _endpoints = new();
    private readonly ManualUiDispatcher _dispatcher = new();

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The view model takes ownership of the renderer and disposes it.")]
    private ShellViewModel Create() =>
        new(_endpoints, new FakeSwitchingEngine(), new FakeConfigStore(), new NullTrayIconRenderer(), _dispatcher);

    [Fact]
    public void The_tooltip_names_the_current_default_device()
    {
        _endpoints.Endpoints.Add(new AudioEndpoint("speakers", "Speakers (Realtek)", true, true, true));
        _endpoints.Defaults = new DefaultEndpoints("speakers", "speakers");

        using var viewModel = Create();

        Assert.Equal("Dongled — playing to Speakers (Realtek)", viewModel.TrayToolTip);
    }

    [Fact]
    public void The_tooltip_follows_a_change_of_default()
    {
        _endpoints.Endpoints.Add(new AudioEndpoint("speakers", "Speakers (Realtek)", true, true, true));
        _endpoints.Endpoints.Add(new AudioEndpoint("headset", "Headset", true, false, false));
        _endpoints.Defaults = new DefaultEndpoints("speakers", "speakers");
        using var viewModel = Create();

        _endpoints.Defaults = new DefaultEndpoints("headset", "headset");
        _endpoints.RaiseDefaultChanged(AudioRole.Media, "headset");

        Assert.Equal("Dongled — playing to Headset", viewModel.TrayToolTip);
    }

    [Fact]
    public void A_tooltip_longer_than_Windows_allows_is_cut_with_an_ellipsis()
    {
        _endpoints.Endpoints.Add(new AudioEndpoint("long", new string('x', 200), true, true, true));
        _endpoints.Defaults = new DefaultEndpoints("long", "long");

        using var viewModel = Create();

        Assert.Equal(127, viewModel.TrayToolTip.Length);
        Assert.EndsWith("…", viewModel.TrayToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void Unreadable_audio_is_described_rather_than_thrown()
    {
        _endpoints.EnumerateFailure = new InvalidOperationException("no audio");

        using var viewModel = Create();

        Assert.Equal("Dongled — playing to unavailable", viewModel.TrayToolTip);
        Assert.NotNull(viewModel.TrayIcon);
    }
}
