using System.Collections.Generic;
using Dongled.Core.Audio;
using Dongled.Core.Interop;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dongled.Core.Tests.Audio;

public class EndpointNotificationClientTests
{
    private readonly List<string?> _activated = [];

    [Fact]
    public void An_endpoint_turning_active_is_forwarded()
    {
        var client = Client(_activated.Add);

        Assert.Equal(0, client.OnDeviceStateChanged("speaker", DeviceState.Active));

        Assert.Equal(["speaker"], _activated);
    }

    [Theory]
    [InlineData(DeviceState.Unplugged)]
    [InlineData(DeviceState.Disabled)]
    [InlineData(DeviceState.NotPresent)]
    internal void An_endpoint_turning_anything_but_active_is_not_forwarded(DeviceState state)
    {
        var client = Client(_activated.Add);

        client.OnDeviceStateChanged("speaker", state);

        Assert.Empty(_activated);
    }

    [Fact]
    public void An_endpoint_being_added_is_forwarded()
    {
        var client = Client(_activated.Add);

        client.OnDeviceAdded("speaker");

        Assert.Equal(["speaker"], _activated);
    }

    [Fact]
    public void A_throwing_handler_still_returns_success_to_windows()
    {
        var client = Client(_ => throw new InvalidOperationException("handler failure"));

        Assert.Equal(0, client.OnDeviceStateChanged("speaker", DeviceState.Active));
        Assert.Equal(0, client.OnDeviceAdded("speaker"));
    }

    private static EndpointNotificationClient Client(Action<string?> onActivated) =>
        new((_, _, _) => { }, onActivated, NullLogger.Instance);
}
