using System.IO;
using Dongled.Abstractions;
using Dongled.Core.Configuration;
using Dongled.Core.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dongled.Core.Tests.Plugins;

/// <summary>
/// The bundled plugins, loaded by the real loader from a real directory, exactly as the app loads
/// them. Plugins.Tests references the plugins directly and so cannot exercise this path.
/// </summary>
public sealed class PortedPluginLoadTests
{
    /// <summary>
    /// Directory name, provider id, provider display name. Deliberately no paths: theory data appears in
    /// a test's displayed name, and an absolute path there would put the repository's location into any
    /// captured test output.
    /// </summary>
    public static TheoryData<string, string, string> PortedPlugins => new()
    {
        { "HyperXHid", "hyperx.cloud3s.hid", "HyperX Cloud III S Wireless (HID)" },
        { "Logitech", "logitech.ghub", "Logitech G HUB" },
    };

    [Theory]
    [MemberData(nameof(PortedPlugins))]
    public void A_ported_plugin_loads_and_its_provider_is_the_hosts_own_interface_type(
        string directoryName,
        string expectedId,
        string expectedDisplayName)
    {
        using var root = new TemporaryPluginRoot();
        var directory = root.AddBuiltPlugin(TemporaryPluginRoot.PortedPluginSource(directoryName), directoryName);

        var results = NewLoader().LoadAll(root.Path, [Approve(directoryName, directory)]);

        var result = Assert.Single(results);
        Assert.Equal(PluginLoadStatus.Loaded, result.Status);

        using var plugin = result.Plugin;
        Assert.NotNull(plugin);

        // Each plugin folder contains its own Dongled.Abstractions.dll. If the load context
        // had resolved that copy, this would be a different type of the same name and the provider
        // would not be assignable to the host's interface at all.
        Assert.IsAssignableFrom<IAudioSourceProvider>(plugin.Provider);
        Assert.Equal(expectedId, plugin.Provider.Metadata.Id);
        Assert.Equal(expectedDisplayName, plugin.Provider.Metadata.DisplayName);
        Assert.False(plugin.Provider.Metadata.IsExperimental);
    }

    [Theory]
    [MemberData(nameof(PortedPlugins))]
    public void The_abstractions_assembly_a_ported_plugin_binds_to_is_the_one_the_host_already_loaded(
        string directoryName,
        string expectedId,
        string expectedDisplayName)
    {
        _ = expectedId;
        _ = expectedDisplayName;

        using var root = new TemporaryPluginRoot();
        var directory = root.AddBuiltPlugin(TemporaryPluginRoot.PortedPluginSource(directoryName), directoryName);

        var results = NewLoader().LoadAll(root.Path, [Approve(directoryName, directory)]);
        using var plugin = Assert.Single(results).Plugin;
        Assert.NotNull(plugin);

        var providerInterface = plugin.Provider.GetType().GetInterface(typeof(IAudioSourceProvider).FullName!);
        Assert.NotNull(providerInterface);
        Assert.Same(typeof(IAudioSourceProvider).Assembly, providerInterface.Assembly);
    }

    [Fact]
    public void The_HID_plugins_own_dependency_is_staged_beside_it()
    {
        // CopyLocalLockFileAssemblies is what puts HidSharp.dll in the plugin's output. Without it the
        // plugin loads and then fails at its first HID call, which no load-time check would catch, and
        // AssemblyDependencyResolver would have nothing to resolve.
        using var root = new TemporaryPluginRoot();
        var directory = root.AddBuiltPlugin(TemporaryPluginRoot.PortedPluginSource("HyperXHid"), "HyperXHid");

        Assert.True(File.Exists(Path.Combine(directory, "HidSharp.dll")));
        Assert.True(File.Exists(Path.Combine(directory, "Dongled.Plugin.HyperXHid.deps.json")));
    }

    [Theory]
    [MemberData(nameof(PortedPlugins))]
    public void A_rebuilt_ported_plugin_is_blocked_until_it_is_approved_again(
        string directoryName,
        string expectedId,
        string expectedDisplayName)
    {
        _ = expectedId;
        _ = expectedDisplayName;

        using var root = new TemporaryPluginRoot();
        var directory = root.AddBuiltPlugin(TemporaryPluginRoot.PortedPluginSource(directoryName), directoryName);
        var approval = Approve(directoryName, directory);

        // Any change at all to any file in the directory, because the manifest hashes the directory as
        // one unit. Appending to the .deps.json stands in for a rebuild.
        var deps = Directory.GetFiles(directory, "*.deps.json").Single();
        File.AppendAllText(deps, " ");

        var result = Assert.Single(NewLoader().LoadAll(root.Path, [approval]));

        Assert.Equal(PluginLoadStatus.Changed, result.Status);
        Assert.Null(result.Plugin);
    }

    [Fact]
    public void Neither_ported_plugin_sits_beside_the_test_assembly_where_the_default_context_probes()
    {
        // ReferenceOutputAssembly="false" is what keeps them out. A real reference would put each plugin
        // next to this assembly, where the default load context resolves it, and every test above would
        // be exercising the default context instead of the plugin's own.
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "Dongled.Plugin.HyperXHid.dll")));
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "Dongled.Plugin.Logitech.dll")));
    }

    private static PluginLoader NewLoader() => new(NullLogger<PluginLoader>.Instance);

    private static PluginConfig Approve(string directory, string pluginPath) => new()
    {
        Directory = directory,
        Enabled = true,
        ManifestSha256 = PluginManifest.Compute(pluginPath).Sha256,
        ApprovedUtc = DateTimeOffset.UtcNow,
    };
}
