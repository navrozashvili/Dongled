using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Xunit;

namespace Dongled.Core.Tests.Plugins;

public sealed class SamplePluginBuildTests
{
    [Fact]
    public void The_sample_plugin_is_staged_beside_the_tests_in_a_directory_nothing_probes()
    {
        Assert.True(
            Directory.Exists(TemporaryPluginRoot.SampleSourceDirectory),
            "The StageSamplePlugin target in the test csproj should have copied the sample plugin here.");

        Assert.True(File.Exists(Path.Combine(
            TemporaryPluginRoot.SampleSourceDirectory,
            "Dongled.Plugin.Sample.dll")));
    }

    [Fact]
    public void The_sample_plugin_carries_its_own_copy_of_the_abstractions_assembly()
    {
        // This is not incidental. It is the condition the load context has to survive: if that
        // copy were loaded into the plugin context, the plugin's IAudioSourceProvider would be a
        // different type from the host's and discovery would fail with a confusing message.
        Assert.True(File.Exists(Path.Combine(
            TemporaryPluginRoot.SampleSourceDirectory,
            "Dongled.Abstractions.dll")));
    }

    [Fact]
    public void The_sample_plugin_is_not_beside_the_test_assembly_where_the_default_context_probes()
    {
        Assert.False(File.Exists(Path.Combine(
            AppContext.BaseDirectory,
            "Dongled.Plugin.Sample.dll")));

        // Only the default context: loader tests in other classes run in parallel and load the
        // sample into their own collectible contexts, which AppDomain.GetAssemblies() also lists.
        Assert.DoesNotContain(
            AssemblyLoadContext.Default.Assemblies,
            assembly => assembly.GetName().Name == "Dongled.Plugin.Sample");
    }

    [Fact]
    public void A_temporary_plugin_root_receives_the_whole_sample_directory()
    {
        // The loader tests all work on a copy so that one of them corrupting a plugin cannot
        // affect another. If the copy dropped a file, every manifest in every loader test would
        // be computed over something other than what the sample plugin actually builds.
        using var root = new TemporaryPluginRoot();
        var directory = root.AddSample();

        var staged = Directory.GetFiles(TemporaryPluginRoot.SampleSourceDirectory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(TemporaryPluginRoot.SampleSourceDirectory, file))
            .Order(StringComparer.Ordinal);

        var copied = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(directory, file))
            .Order(StringComparer.Ordinal);

        Assert.Equal(staged, copied);
        Assert.Equal(
            Path.Combine(directory, "Dongled.Plugin.Sample.dll"),
            TemporaryPluginRoot.MainAssembly(directory));
    }

    [Fact]
    public void The_sample_plugin_references_only_the_sdk()
    {
        // A plugin that referenced the host would not be a plugin, and Abstractions is kept
        // dependency-free so an author is never coupled to the host's logging version.
        var path = Path.Combine(
            TemporaryPluginRoot.SampleSourceDirectory,
            "Dongled.Plugin.Sample.dll");

        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();

        var referenced = metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .ToList();

        Assert.Contains("Dongled.Abstractions", referenced);
        Assert.DoesNotContain("Dongled.Core", referenced);
    }
}
