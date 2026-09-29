using System.IO;
using Dongled.Core.Plugins;
using Xunit;

namespace Dongled.Core.Tests.Plugins;

public sealed class PluginAssemblyIdentityTests
{
    [Fact]
    public void A_real_plugin_reports_its_name_and_its_sdk_reference()
    {
        var assembly = TemporaryPluginRoot.MainAssembly(TemporaryPluginRoot.SampleSourceDirectory);
        using var stream = File.OpenRead(assembly);

        Assert.True(PluginAssemblyIdentity.TryRead(stream, out var identity, out var failure));
        Assert.Equal(string.Empty, failure);
        Assert.NotNull(identity);
        Assert.Equal("Dongled.Plugin.Sample", identity.Name);
        Assert.NotNull(identity.SdkVersion);
    }

    [Fact]
    public void A_file_that_is_not_a_pe_image_is_refused()
    {
        using var stream = new MemoryStream("not an assembly"u8.ToArray());

        Assert.False(PluginAssemblyIdentity.TryRead(stream, out var identity, out var failure));
        Assert.Null(identity);
        Assert.NotEqual(string.Empty, failure);
    }

    [Fact]
    public void The_stream_is_left_open_and_rewindable_for_the_caller_that_still_needs_it()
    {
        var assembly = TemporaryPluginRoot.MainAssembly(TemporaryPluginRoot.SampleSourceDirectory);
        using var stream = File.OpenRead(assembly);

        Assert.True(PluginAssemblyIdentity.TryRead(stream, out _, out _));

        // The loader holds one handle across its whole decision and seeks back to zero before
        // handing the bytes to the load context. If this read closed the stream, that would throw.
        stream.Seek(0, SeekOrigin.Begin);
        Assert.Equal(0, stream.Position);
    }
}
