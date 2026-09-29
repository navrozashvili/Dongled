using Dongled.Core.Plugins;
using Xunit;

namespace Dongled.Core.Tests.Plugins;

public sealed class PluginPackageRulesTests
{
    [Theory]
    [InlineData("MyPlugin.zip", "MyPlugin")]
    [InlineData("My Plugin v2.zip", "My Plugin v2")]
    [InlineData("weird:name*.zip", "weirdname")]
    public void A_usable_folder_name_is_taken_from_the_file_name(string fileName, string expected)
    {
        Assert.True(PluginPackageRules.TrySanitiseDirectoryName(fileName, out var name, out _));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("CON.zip")]
    [InlineData("nul.zip")]
    [InlineData("COM1.zip")]
    [InlineData("....zip")]
    [InlineData("***.zip")]
    [InlineData("")]
    public void A_folder_name_Windows_or_the_config_file_could_not_hold_is_refused(string fileName)
    {
        Assert.False(PluginPackageRules.TrySanitiseDirectoryName(fileName, out _, out var failure));
        Assert.NotEqual(string.Empty, failure);
    }
}
