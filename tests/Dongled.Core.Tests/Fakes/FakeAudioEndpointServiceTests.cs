using Dongled.Core.Audio;
using Dongled.Core.Configuration;
using Xunit;

namespace Dongled.Core.Tests.Fakes;

/// <summary>
/// The fake is the instrument every policy test reads, so its own behaviour is worth pinning:
/// a fake that silently accepts a switch it should have rejected turns a real defect into a
/// passing suite.
/// </summary>
public class FakeAudioEndpointServiceTests
{
    [Fact]
    public void Setting_a_default_records_the_endpoint_and_the_roles_and_updates_what_is_reported()
    {
        var endpoints = new FakeAudioEndpointService();
        endpoints.AddActive("speakers", "Speakers");
        endpoints.AddActive("headset", "Headset");
        endpoints.Defaults = new DefaultEndpoints("speakers", "speakers");

        var applied = endpoints.SetDefault("headset", [AudioRole.Media]);

        Assert.True(applied);
        var call = Assert.Single(endpoints.SetCalls);
        Assert.Equal("headset", call.EndpointId);
        Assert.Equal([AudioRole.Media], call.Roles);
        Assert.Equal(new DefaultEndpoints("headset", "speakers"), endpoints.GetDefaults());
    }

    [Fact]
    public void An_endpoint_it_does_not_know_is_refused_when_asked_to_behave_like_windows()
    {
        var endpoints = new FakeAudioEndpointService { RejectUnknownEndpoints = true };
        endpoints.AddActive("speakers", "Speakers");
        endpoints.Defaults = new DefaultEndpoints("speakers", "speakers");

        var applied = endpoints.SetDefault("gone", [AudioRole.Media, AudioRole.Calls]);

        Assert.False(applied);
        Assert.Empty(endpoints.SetCalls);
        Assert.Equal(new DefaultEndpoints("speakers", "speakers"), endpoints.GetDefaults());
    }

    [Fact]
    public void Enumeration_can_be_made_to_fail_so_the_policy_can_be_tested_against_a_dead_audio_service()
    {
        var endpoints = new FakeAudioEndpointService { ThrowOnEnumerate = true };

        Assert.Throws<System.Runtime.InteropServices.COMException>(endpoints.Enumerate);
    }

    [Fact]
    public void The_default_changed_event_reaches_a_subscriber()
    {
        var endpoints = new FakeAudioEndpointService();
        DefaultChangedEventArgs? seen = null;
        endpoints.DefaultChanged += (_, args) => seen = args;

        endpoints.RaiseDefaultChanged(AudioRole.Calls, "headset");

        Assert.NotNull(seen);
        Assert.Equal(AudioRole.Calls, seen.Role);
        Assert.Equal("headset", seen.EndpointId);
    }
}
