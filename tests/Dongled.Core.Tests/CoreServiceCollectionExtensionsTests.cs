using System.Linq;
using Dongled.Core.Audio;
using Dongled.Core.Configuration;
using Dongled.Core.Engine;
using Dongled.Core.Plugins;
using Dongled.Core.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dongled.Core.Tests;

public class CoreServiceCollectionExtensionsTests
{
    [Fact]
    public void Every_registration_the_engine_needs_can_be_resolved()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // The real endpoint service talks to Windows audio in its constructor, so the test
        // substitutes for it before the defaults are added. That the substitution wins is itself
        // worth asserting: a design-time preview needs the same ability.
        services.AddSingleton<IAudioEndpointService, FakeAudioEndpointService>();
        services.AddDongledCore();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<FakeAudioEndpointService>(provider.GetRequiredService<IAudioEndpointService>());
        Assert.IsType<ConfigStore>(provider.GetRequiredService<IConfigStore>());
        Assert.IsType<StateStore>(provider.GetRequiredService<IStateStore>());
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        Assert.NotNull(provider.GetRequiredService<SwitchingEngine>());
    }

    [Fact]
    public void The_engine_is_a_singleton_because_it_owns_all_the_state()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAudioEndpointService, FakeAudioEndpointService>();
        services.AddDongledCore();

        using var provider = services.BuildServiceProvider();

        // Two engines would be two owners of presence and two writers of state.json.
        Assert.Same(provider.GetRequiredService<SwitchingEngine>(), provider.GetRequiredService<SwitchingEngine>());
    }

    [Fact]
    public void Registering_twice_does_not_duplicate_anything()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAudioEndpointService, FakeAudioEndpointService>();

        services.AddDongledCore();
        services.AddDongledCore();

        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<IConfigStore>());
    }

    [Fact]
    public async Task The_plugin_loader_and_host_are_registered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAudioEndpointService, FakeAudioEndpointService>();
        services.AddDongledCore();

        // Await-using, not using. PluginHost is IAsyncDisposable only, and a container that has
        // built one refuses a synchronous Dispose outright. Whoever composes the app hits the
        // same rule, which is why PluginHost says so in its own documentation.
        await using var provider = services.BuildServiceProvider();

        Assert.IsType<PluginLoader>(provider.GetRequiredService<IPluginLoader>());
        Assert.NotNull(provider.GetRequiredService<PluginHost>());
    }

    [Fact]
    public async Task A_substituted_plugin_loader_wins_and_the_host_is_given_it()
    {
        // TryAdd is only meaningful if a substitution survives, and a host that resolved its own
        // loader rather than the container's would quietly defeat it. Both are needed for a
        // design-time preview that must not touch the file system.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAudioEndpointService, FakeAudioEndpointService>();
        services.AddSingleton<IPluginLoader, LoaderThatFindsNothing>();
        services.AddDongledCore();

        await using var provider = services.BuildServiceProvider();

        var loader = Assert.IsType<LoaderThatFindsNothing>(provider.GetRequiredService<IPluginLoader>());
        var host = provider.GetRequiredService<PluginHost>();

        // Resolving both is not enough to show the host was given the container's loader; it has
        // to be run, and the substitute has to be the thing that gets asked.
        using var stopping = new CancellationTokenSource();
        var run = host.RunAsync(stopping.Token);
        await host.Started;
        await stopping.CancelAsync();
        await run;

        Assert.Equal(1, loader.Calls);
        Assert.Empty(host.Results);
    }

    [Fact]
    public async Task The_host_is_a_singleton_because_it_owns_the_loaded_plugins()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAudioEndpointService, FakeAudioEndpointService>();
        services.AddDongledCore();

        await using var provider = services.BuildServiceProvider();

        // Two hosts would load the same plugin assembly into two contexts and hand out two
        // providers with the same identifier.
        Assert.Same(provider.GetRequiredService<PluginHost>(), provider.GetRequiredService<PluginHost>());
    }

    [Fact]
    public async Task The_plugin_installer_is_registered_and_is_a_singleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAudioEndpointService, FakeAudioEndpointService>();
        services.AddDongledCore();

        await using var provider = services.BuildServiceProvider();

        // A singleton for the same reason the host is: it owns the staging directory, and two of
        // them sweeping it at startup would each be deleting folders the other was still filling.
        Assert.Same(
            provider.GetRequiredService<PluginInstaller>(),
            provider.GetRequiredService<PluginInstaller>());
    }

    [Fact]
    public async Task Resolving_the_installer_does_not_build_the_host()
    {
        // The installer asks the host whether a directory's provider is running. Were that asked
        // eagerly, resolving the installer would construct a PluginHost - and the container would
        // then refuse a synchronous Dispose, so a caller that only ever wanted to inspect a zip
        // would inherit an async teardown it has no reason to know about. Resolving it inside the
        // delegate is what keeps the two independent, and a synchronous using here is what proves
        // the host was not built.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAudioEndpointService, FakeAudioEndpointService>();
        services.AddDongledCore();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<PluginInstaller>());

        await Task.CompletedTask;
    }

    private sealed class LoaderThatFindsNothing : IPluginLoader
    {
        public int Calls { get; private set; }

        public IReadOnlyList<PluginLoadResult> LoadAll(
            string pluginsDirectory,
            IReadOnlyList<PluginConfig> trust)
        {
            Calls++;
            return [];
        }
    }
}
