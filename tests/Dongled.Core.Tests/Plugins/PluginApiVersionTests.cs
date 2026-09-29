using Dongled.Abstractions;
using Dongled.Core.Plugins;
using Xunit;

namespace Dongled.Core.Tests.Plugins;

public sealed class PluginApiVersionTests
{
    [Fact]
    public void The_host_version_is_the_one_the_abstractions_project_declares()
    {
        // The csproj's Version element is the single source of truth for this check, so the host
        // must read it from the assembly rather than repeating it.
        Assert.Equal(
            typeof(IAudioSourceProvider).Assembly.GetName().Version,
            PluginApiVersion.Host);
    }

    [Theory]
    // Same version: obviously fine.
    [InlineData(1, 0, 1, 0, true)]
    // Built against an older additive minor: everything it uses still exists.
    [InlineData(1, 0, 1, 3, true)]
    [InlineData(1, 2, 1, 3, true)]
    // Built against a newer minor: it may use members this host does not have.
    [InlineData(1, 4, 1, 3, false)]
    [InlineData(1, 1, 1, 0, false)]
    // Different major: breaking by definition, in either direction.
    [InlineData(2, 0, 1, 0, false)]
    [InlineData(1, 0, 2, 0, false)]
    [InlineData(0, 9, 1, 0, false)]
    public void Compatibility_allows_an_older_additive_minor_and_nothing_else(
        int pluginMajor,
        int pluginMinor,
        int hostMajor,
        int hostMinor,
        bool expected)
    {
        Assert.Equal(
            expected,
            PluginApiVersion.IsCompatible(
                new Version(pluginMajor, pluginMinor, 0, 0),
                new Version(hostMajor, hostMinor, 0, 0)));
    }

    [Fact]
    public void The_build_and_revision_numbers_are_not_part_of_the_decision()
    {
        // Only the Version element's major and minor are meaningful; the SDK fills the rest in.
        Assert.True(PluginApiVersion.IsCompatible(new Version(1, 0, 7, 99), new Version(1, 0, 0, 0)));
    }

    [Fact]
    public void A_version_is_described_by_major_and_minor_only()
    {
        Assert.Equal("1.0", PluginApiVersion.Describe(new Version(1, 0, 0, 0)));
        Assert.Equal("2.3", PluginApiVersion.Describe(new Version(2, 3, 9, 9)));
    }
}
