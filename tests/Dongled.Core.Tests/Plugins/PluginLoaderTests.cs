using System.IO;
using Dongled.Abstractions;
using Dongled.Core.Configuration;
using Dongled.Core.Plugins;
using Dongled.Core.Tests.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dongled.Core.Tests.Plugins;

public sealed class PluginLoaderTests
{
    [Fact]
    public void An_approved_plugin_loads_and_its_provider_is_the_hosts_own_interface_type()
    {
        using var root = new TemporaryPluginRoot();
        var directory = root.AddSample();

        var results = NewLoader().LoadAll(root.Path, [Approve("Sample", directory)]);

        var result = Assert.Single(results);
        Assert.Equal(PluginLoadStatus.Loaded, result.Status);

        using var plugin = result.Plugin;
        Assert.NotNull(plugin);

        // The plugin folder contains its own Dongled.Abstractions.dll. If the load
        // context had resolved that copy, this cast would be to a different type of the same name
        // and the provider would not be assignable to the host's interface at all.
        Assert.IsAssignableFrom<IAudioSourceProvider>(plugin.Provider);
        Assert.Equal("sample.demo", plugin.Provider.Metadata.Id);
        Assert.Equal("Sample provider", plugin.Provider.Metadata.DisplayName);
        Assert.True(plugin.Provider.Metadata.IsExperimental);
    }

    [Fact]
    public void Each_outcome_is_logged_as_one_readable_sentence()
    {
        using var root = new TemporaryPluginRoot();
        var approved = root.AddSample();
        root.AddSample("Unapproved");
        var logger = new RecordingLogger();

        var results = new PluginLoader(new TypedLogger<PluginLoader>(logger))
            .LoadAll(root.Path, [Approve("Sample", approved)]);
        foreach (var result in results)
        {
            result.Plugin?.Dispose();
        }

        var messages = logger.Entries.Select(entry => entry.Message).ToArray();
        Assert.Contains("Plugin Sample loaded.", messages);
        Assert.Contains(
            messages,
            message => message.StartsWith("Plugin Unapproved was not loaded (Unapproved): ", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => message.Contains("Loaded. Loaded.", StringComparison.Ordinal));
    }

    [Fact]
    public void The_abstractions_assembly_the_plugin_binds_to_is_the_one_the_host_already_loaded()
    {
        using var root = new TemporaryPluginRoot();
        var directory = root.AddSample();

        var results = NewLoader().LoadAll(root.Path, [Approve("Sample", directory)]);
        using var plugin = Assert.Single(results).Plugin;
        Assert.NotNull(plugin);

        var providerInterface = plugin.Provider.GetType().GetInterface(typeof(IAudioSourceProvider).FullName!);
        Assert.NotNull(providerInterface);
        Assert.Same(typeof(IAudioSourceProvider).Assembly, providerInterface.Assembly);
    }

    [Fact]
    public void Loading_leaves_the_plugin_files_writable_so_a_disabled_plugin_can_be_replaced()
    {
        // LoadFromStream copies rather than maps, and the handle that denied writers during the
        // decision is closed by the time LoadAll returns. If a plugin's own DLL stayed locked, a
        // user could not update a plugin without stopping the app.
        using var root = new TemporaryPluginRoot();
        var directory = root.AddSample();

        var results = NewLoader().LoadAll(root.Path, [Approve("Sample", directory)]);
        using var plugin = Assert.Single(results).Plugin;
        Assert.NotNull(plugin);

        using var writer = new FileStream(
            TemporaryPluginRoot.MainAssembly(directory),
            FileMode.Open,
            FileAccess.Write,
            FileShare.None);

        Assert.True(writer.CanWrite);
    }

    [Fact]
    public void The_manifest_is_reported_whether_or_not_the_plugin_loaded()
    {
        using var root = new TemporaryPluginRoot();
        var directory = root.AddSample();

        var loaded = Assert.Single(NewLoader().LoadAll(root.Path, [Approve("Sample", directory)]));
        var unapproved = Assert.Single(NewLoader().LoadAll(root.Path, []));

        using var plugin = loaded.Plugin;

        Assert.NotNull(loaded.Manifest);
        Assert.NotNull(unapproved.Manifest);

        // The UI needs the current hash to offer approval, which is exactly the case where the
        // plugin did not load.
        Assert.Equal(loaded.Manifest.Sha256, unapproved.Manifest.Sha256);
        Assert.Contains(
            unapproved.Manifest.Files,
            file => file.RelativePath == "Dongled.Plugin.Sample.dll");
    }

    [Fact]
    public void Every_directory_under_the_root_gets_a_result_in_a_stable_order()
    {
        using var root = new TemporaryPluginRoot();
        root.AddDirectory("zeta");
        root.AddSample("Sample");
        root.AddDirectory("alpha");

        var results = NewLoader().LoadAll(root.Path, []);

        Assert.Equal(["alpha", "Sample", "zeta"], results.Select(result => result.Directory));
    }

    [Fact]
    public void A_plugins_root_that_does_not_exist_yields_nothing_rather_than_throwing()
    {
        using var root = new TemporaryPluginRoot();

        var results = NewLoader().LoadAll(Path.Combine(root.Path, "absent"), []);

        Assert.Empty(results);
    }

    [Fact]
    public void A_directory_with_no_candidate_assembly_is_listed_as_not_a_plugin()
    {
        using var root = new TemporaryPluginRoot();
        var directory = root.AddDirectory("vendor-files");
        File.WriteAllText(Path.Combine(directory, "readme.txt"), "just some files");

        var result = Assert.Single(NewLoader().LoadAll(root.Path, []));

        Assert.Equal(PluginLoadStatus.NotAPlugin, result.Status);
        Assert.Null(result.Plugin);
    }

    [Fact]
    public void Two_candidate_assemblies_in_one_directory_fail_rather_than_one_being_picked()
    {
        using var root = new TemporaryPluginRoot();
        var directory = root.AddSample();
        File.Copy(
            TemporaryPluginRoot.MainAssembly(directory),
            Path.Combine(directory, "Dongled.Plugin.Second.dll"));

        var result = Assert.Single(NewLoader().LoadAll(root.Path, []));

        Assert.Equal(PluginLoadStatus.Failed, result.Status);
        Assert.Contains("exactly one", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plugin_built_against_an_incompatible_sdk_fails_with_both_versions_named()
    {
        // Driven by telling the loader it is a 2.0 host, which is the same comparison the shipping
        // loader makes against the version it reads out of the plugin's metadata. Shipping a real
        // 2.0.0 artifact would mean a second copy of the Abstractions source in this repository.
        using var root = new TemporaryPluginRoot();
        var directory = root.AddSample();

        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new Version(2, 0, 0, 0));
        var result = Assert.Single(loader.LoadAll(root.Path, [Approve("Sample", directory)]));

        Assert.Equal(PluginLoadStatus.Failed, result.Status);
        Assert.Contains("1.0", result.Message, StringComparison.Ordinal);
        Assert.Contains("2.0", result.Message, StringComparison.Ordinal);
        Assert.Null(result.Plugin);
    }

    [Fact]
    public void An_assembly_that_does_not_reference_the_sdk_at_all_fails_clearly()
    {
        using var root = new TemporaryPluginRoot();
        var directory = root.AddDirectory("NotAnSdkPlugin");

        // Abstractions references nothing of ours, so it stands in for an assembly that happens to
        // be named like a plugin without being one.
        File.Copy(
            Path.Combine(TemporaryPluginRoot.SampleSourceDirectory, "Dongled.Abstractions.dll"),
            Path.Combine(directory, "Dongled.Plugin.Impostor.dll"));

        var trust = new PluginConfig
        {
            Directory = "NotAnSdkPlugin",
            Enabled = true,
            ManifestSha256 = PluginManifest.Compute(directory).Sha256,
        };

        var result = Assert.Single(NewLoader().LoadAll(root.Path, [trust]));

        Assert.Equal(PluginLoadStatus.Failed, result.Status);
        Assert.Contains("Dongled.Abstractions", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_assembly_without_the_entry_point_attribute_fails_with_the_attribute_named()
    {
        using var root = new TemporaryPluginRoot();
        var directory = root.AddDirectory("NoAttribute");

        // Core references Abstractions 1.0.0 and carries no AudioSourceProvider attribute, so it
        // reaches the attribute step and fails there rather than earlier.
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Dongled.Core.dll"),
            Path.Combine(directory, "Dongled.Plugin.NoAttribute.dll"));

        var trust = new PluginConfig
        {
            Directory = "NoAttribute",
            Enabled = true,
            ManifestSha256 = PluginManifest.Compute(directory).Sha256,
        };

        var result = Assert.Single(NewLoader().LoadAll(root.Path, [trust]));

        Assert.Equal(PluginLoadStatus.Failed, result.Status);
        Assert.Contains("AudioSourceProvider", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(NoParameterlessConstructor), "parameterless")]
    [InlineData(typeof(ConstructorThrows), "threw")]
    [InlineData(typeof(NotAProvider), "IAudioSourceProvider")]
    [InlineData(typeof(AbstractProvider), "abstract")]
    public void A_declared_type_that_cannot_be_used_is_rejected_with_a_reason(Type type, string expected)
    {
        Assert.False(PluginLoader.TryCreateProvider(type, out var provider, out var failure));
        Assert.Null(provider);
        Assert.Contains(expected, failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_usable_declared_type_is_constructed()
    {
        Assert.True(PluginLoader.TryCreateProvider(typeof(UsableProvider), out var provider, out var failure));
        Assert.NotNull(provider);
        Assert.Equal(string.Empty, failure);
    }

    private static PluginLoader NewLoader() => new(NullLogger<PluginLoader>.Instance);

    private static PluginConfig Approve(string directory, string pluginPath) => new()
    {
        Directory = directory,
        Enabled = true,
        ManifestSha256 = PluginManifest.Compute(pluginPath).Sha256,
        ApprovedUtc = DateTimeOffset.UtcNow,
    };

    /// <summary>Hands a <see cref="RecordingLogger"/> to something that wants a typed logger.</summary>
    private sealed class TypedLogger<T>(RecordingLogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private sealed class NoParameterlessConstructor : IAudioSourceProvider
    {
        public NoParameterlessConstructor(int unused) => Unused = unused;

        public ProviderMetadata Metadata { get; } = new("x", "X", null, false);

        public int Unused { get; }

        public Task StartAsync(IProviderContext context, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ConstructorThrows : IAudioSourceProvider
    {
        public ConstructorThrows() => throw new InvalidOperationException("from a plugin constructor");

        public ProviderMetadata Metadata { get; } = new("x", "X", null, false);

        public Task StartAsync(IProviderContext context, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NotAProvider
    {
    }

    private abstract class AbstractProvider : IAudioSourceProvider
    {
        public ProviderMetadata Metadata { get; } = new("x", "X", null, false);

        public Task StartAsync(IProviderContext context, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class UsableProvider : IAudioSourceProvider
    {
        public ProviderMetadata Metadata { get; } = new("x", "X", null, false);

        public Task StartAsync(IProviderContext context, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
