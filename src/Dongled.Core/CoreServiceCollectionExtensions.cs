using Dongled.Core.Audio;
using Dongled.Core.Configuration;
using Dongled.Core.Engine;
using Dongled.Core.Logging;
using Dongled.Core.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Dongled.Core;

/// <summary>Registers the audio, configuration and switching services.</summary>
public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Add everything the switching engine needs. Every registration uses <c>TryAdd</c>, so a
    /// caller that has already substituted a service keeps theirs — which is how a test or a
    /// design-time preview avoids talking to Windows audio.
    /// </summary>
    /// <remarks>
    /// The engine is a singleton because it owns all presence and policy state and is the only
    /// writer of the state file. Two of them would reintroduce the contention spec 4.1 removes.
    /// Nothing here starts anything: whoever composes the app calls
    /// <see cref="PluginHost.RunAsync"/>, which owns both orderings — engine draining before any
    /// provider starts, every provider stopped before the queue closes — so that neither has to be
    /// remembered at the composition site.
    /// </remarks>
    public static IServiceCollection AddDongledCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAudioEndpointService, CoreAudioEndpointService>();

        // Registered here rather than in the app because the lines the UI most needs to show are
        // the engine's and the providers', which are logged from inside this assembly. A caller
        // that wants the buffer wired into its logger factory has to register the same instance
        // itself before calling this, which TryAdd allows.
        services.TryAddSingleton<LogRingBuffer>();

        services.TryAddSingleton<IConfigStore>(provider => new ConfigStore(
            StoragePaths.ConfigFile,
            provider.GetRequiredService<ILogger<ConfigStore>>()));

        // One instance behind both interfaces: the store serializes read-modify-writes with a lock
        // of its own, and two instances would each hold a different one.
        services.TryAddSingleton(provider => new StateStore(
            StoragePaths.StateFile,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<StateStore>>()));
        services.TryAddSingleton<IStateStore>(provider => provider.GetRequiredService<StateStore>());
        services.TryAddSingleton<IUpdateStateStore>(provider => provider.GetRequiredService<StateStore>());

        services.TryAddSingleton<IWindowPlacementStore>(provider => new WindowPlacementStore(
            StoragePaths.WindowFile,
            provider.GetRequiredService<ILogger<WindowPlacementStore>>()));

        services.TryAddSingleton<SwitchingEngine>();
        services.TryAddSingleton<ISwitchingEngine>(provider => provider.GetRequiredService<SwitchingEngine>());

        // Singleton because loading is a decision about process-wide state: two loaders would
        // load the same plugin assembly into two contexts and hand out two providers with the
        // same identifier.
        services.TryAddSingleton<IPluginLoader, PluginLoader>();

        // Constructed explicitly rather than by convention, because the plugins root is a value
        // rather than a service and the container has no way to supply it.
        services.TryAddSingleton(provider => new PluginHost(
            provider.GetRequiredService<IPluginLoader>(),
            provider.GetRequiredService<SwitchingEngine>(),
            provider.GetRequiredService<IConfigStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILoggerFactory>(),
            StoragePaths.PluginsDirectory));

        // Explicitly, for the same reason the host is: two of its five arguments are paths rather
        // than services. The host is resolved inside the delegate rather than taken as an argument
        // so that asking whether a plugin is running does not make constructing the installer
        // depend on constructing the host - which would drag the host's asynchronous teardown onto
        // anyone who only wanted to inspect a zip. StateOf compares directory names
        // case-insensitively, which is what this delegate is documented to require.
        services.TryAddSingleton(provider => new PluginInstaller(
            provider.GetRequiredService<IConfigStore>(),
            StoragePaths.PluginsDirectory,
            StoragePaths.StagingDirectory,
            directory => provider.GetRequiredService<PluginHost>().StateOf(directory) is not null,
            provider.GetRequiredService<ILogger<PluginInstaller>>()));

        return services;
    }
}
