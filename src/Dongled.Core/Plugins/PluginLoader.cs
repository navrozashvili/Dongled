using System.Globalization;
using System.IO;
using System.Reflection;
using Dongled.Abstractions;
using Dongled.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Plugins;

/// <summary>
/// Decides, failing closed, whether each plugin directory may be loaded, and loads the ones that may.
/// </summary>
/// <remarks>
/// <para>
/// A failure at any step before trust is established produces
/// <see cref="PluginLoadStatus.Blocked"/> and nothing runs. A failure while deciding whether a
/// plugin is permitted must never result in loading it.
/// </para>
/// <para>
/// The candidate assembly is opened exactly once, with <c>FileShare.Read</c>, and that handle is
/// held from the identity check through hashing and loading. Two separate opens would leave a
/// window in which the file could be swapped between being hashed and being loaded.
/// </para>
/// <para>
/// Dependencies beside it are hashed and then closed, and
/// <see cref="System.Runtime.Loader.AssemblyDependencyResolver"/> opens them again when the plugin
/// first needs them. That window is not closed, and it does not need to be: the load context is a versioning boundary, not a sandbox, and an attacker who can write into
/// the plugins directory between two operations of one process can also edit the configuration
/// file that records the approved hash.
/// </para>
/// </remarks>
public sealed class PluginLoader : IPluginLoader
{
    private const string CandidatePattern = "Dongled.Plugin.*.dll";
    private const string SdkAssemblyName = "Dongled.Abstractions";

    private readonly ILogger<PluginLoader> _logger;
    private readonly Version _hostApiVersion;
    private readonly BundledPluginTrust _bundled;

    /// <param name="logger">Where every decision is recorded.</param>
    public PluginLoader(ILogger<PluginLoader> logger)
        : this(logger, PluginApiVersion.Host, BundledPluginTrust.Embedded)
    {
    }

    /// <summary>Construct a loader with a list of shipped plugins other than the embedded one.</summary>
    internal PluginLoader(ILogger<PluginLoader> logger, BundledPluginTrust bundled)
        : this(logger, PluginApiVersion.Host, bundled)
    {
    }

    /// <summary>
    /// Construct a loader that judges compatibility against a version other than the one this
    /// process actually loaded.
    /// </summary>
    /// <remarks>
    /// Exists so the incompatible-SDK path can be exercised against a real plugin assembly. The
    /// alternative would be a second copy of the <c>Abstractions</c> source in the repository,
    /// built at another version, purely to produce a test artifact.
    /// </remarks>
    internal PluginLoader(ILogger<PluginLoader> logger, Version hostApiVersion)
        : this(logger, hostApiVersion, BundledPluginTrust.None)
    {
    }

    private PluginLoader(ILogger<PluginLoader> logger, Version hostApiVersion, BundledPluginTrust bundled)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(hostApiVersion);
        ArgumentNullException.ThrowIfNull(bundled);

        _logger = logger;
        _hostApiVersion = hostApiVersion;
        _bundled = bundled;
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginLoadResult> LoadAll(string pluginsDirectory, IReadOnlyList<PluginConfig> trust)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsDirectory);
        ArgumentNullException.ThrowIfNull(trust);

        List<string> directories;
        try
        {
            if (!Directory.Exists(pluginsDirectory))
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "There is no plugins directory at {Directory}, so no plugin was loaded.",
                        pluginsDirectory);
                }

                return [];
            }

            directories = [.. Directory.EnumerateDirectories(pluginsDirectory)];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The plugins directory could not be listed, so no plugin was loaded.");
            return [];
        }

        // Ordinal, so the Plugins page does not reorder itself between runs on a machine with a
        // different culture.
        directories.Sort(StringComparer.OrdinalIgnoreCase);

        var results = new List<PluginLoadResult>(directories.Count);
        foreach (var directory in directories)
        {
            results.Add(Decide(directory, trust));
        }

        return results;
    }

    /// <summary>
    /// Whether a declared entry-point type can be turned into a provider, and if not, why.
    /// </summary>
    /// <remarks>
    /// Separate from the load so the four ways a declared type can be unusable are testable
    /// without four broken plugin assemblies. <paramref name="failure"/> is the message the UI
    /// shows, so it names the type.
    /// </remarks>
    internal static bool TryCreateProvider(
        Type type,
        out IAudioSourceProvider? provider,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(type);

        provider = null;

        if (type.IsAbstract || type.IsInterface)
        {
            failure = $"The declared provider type '{type.FullName}' is abstract, so the host cannot construct it.";
            return false;
        }

        if (!typeof(IAudioSourceProvider).IsAssignableFrom(type))
        {
            failure = $"The declared provider type '{type.FullName}' does not implement IAudioSourceProvider.";
            return false;
        }

        if (type.GetConstructor(Type.EmptyTypes) is null)
        {
            failure = $"The declared provider type '{type.FullName}' has no public parameterless constructor.";
            return false;
        }

        try
        {
            provider = Activator.CreateInstance(type) as IAudioSourceProvider;
        }
        catch (TargetInvocationException ex)
        {
            failure = $"The declared provider type '{type.FullName}' threw while being constructed: "
                + (ex.InnerException?.Message ?? ex.Message);
            return false;
        }
        catch (MissingMethodException ex)
        {
            failure = $"The declared provider type '{type.FullName}' could not be constructed: {ex.Message}";
            return false;
        }

        if (provider is null)
        {
            failure = $"Constructing the declared provider type '{type.FullName}' produced nothing.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private PluginLoadResult Decide(string directoryPath, IReadOnlyList<PluginConfig> trust)
    {
        var name = Path.GetFileName(directoryPath);

        try
        {
            var result = DecideCore(directoryPath, name, trust);
            Report(result);
            return result;
        }
        catch (Exception ex)
        {
            // Fail-closed. Anything at all that went wrong while deciding means not loading.
            _logger.LogError(
                ex,
                "Plugin {Directory} was blocked because deciding whether it could be loaded failed.",
                name);

            return new PluginLoadResult(
                name,
                PluginLoadStatus.Blocked,
                $"Blocked: deciding whether this plugin could be loaded failed. {ex.Message}");
        }
    }

    private PluginLoadResult DecideCore(string directoryPath, string name, IReadOnlyList<PluginConfig> trust)
    {
        // 1. Is there a candidate at all?
        var candidates = Directory.GetFiles(directoryPath, CandidatePattern, SearchOption.TopDirectoryOnly);

        if (candidates.Length == 0)
        {
            return new PluginLoadResult(
                name,
                PluginLoadStatus.NotAPlugin,
                $"No file here matches {CandidatePattern}.");
        }

        if (candidates.Length > 1)
        {
            return new PluginLoadResult(
                name,
                PluginLoadStatus.Failed,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{candidates.Length} files match {CandidatePattern}; a plugin directory must contain exactly one."));
        }

        var assemblyPath = candidates[0];
        var assemblyName = Path.GetFileName(assemblyPath);

        // 2. One handle, denying writers, held until the decision is finished. Everything below
        //    reads the same bytes.
        using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // 3. Managed assembly, and which SDK version does it reference? Both come off this handle
        //    without loading anything, so an assembly that is never trusted is never loaded.
        if (!PluginAssemblyIdentity.TryRead(stream, out var identity, out var identityFailure))
        {
            return new PluginLoadResult(
                name,
                PluginLoadStatus.NotAPlugin,
                $"'{assemblyName}' {identityFailure}");
        }

        var sdk = identity!.SdkVersion;

        // 4. Hash the whole directory, not just the candidate.
        var manifest = PluginManifest.Compute(directoryPath);

        // 5. Trust. A configuration entry the user switched off wins over everything, including
        //    being shipped with this build. Otherwise either the user approved exactly these files,
        //    or these are exactly the files this build shipped under this name.
        var entry = trust.FirstOrDefault(
            plugin => string.Equals(plugin.Directory, name, StringComparison.OrdinalIgnoreCase));
        var bundled = _bundled.Covers(name, manifest.Sha256);

        if (entry is { Enabled: false })
        {
            return new PluginLoadResult(name, PluginLoadStatus.Disabled, "Switched off.", manifest, isBundled: bundled);
        }

        if (!bundled)
        {
            if (entry is null)
            {
                return new PluginLoadResult(
                    name,
                    PluginLoadStatus.Unapproved,
                    "This plugin has not been approved. Approve it to record a hash of its files and allow it to load.",
                    manifest);
            }

            if (!manifest.Matches(entry.ManifestSha256))
            {
                return new PluginLoadResult(
                    name,
                    PluginLoadStatus.Changed,
                    "The files here have changed since this plugin was approved. Approve it again to allow it to load.",
                    manifest);
            }
        }

        // 6. Compatibility, before anything touches a type. Getting this after the attribute check
        //    turns "built for 2.0, this host provides 1.0" into a FileNotFoundException naming an
        //    assembly the user has never heard of.
        if (sdk is null)
        {
            return new PluginLoadResult(
                name,
                PluginLoadStatus.Failed,
                $"'{assemblyName}' does not reference {SdkAssemblyName}, so it is not built against the plugin SDK.",
                manifest,
                isBundled: bundled);
        }

        if (!PluginApiVersion.IsCompatible(sdk, _hostApiVersion))
        {
            return new PluginLoadResult(
                name,
                PluginLoadStatus.Failed,
                $"Built for {SdkAssemblyName} {PluginApiVersion.Describe(sdk)}; this host provides "
                    + $"{PluginApiVersion.Describe(_hostApiVersion)}.",
                manifest,
                isBundled: bundled);
        }

        return LoadTrusted(name, assemblyPath, assemblyName, stream, manifest, bundled);
    }

    private PluginLoadResult LoadTrusted(
        string name,
        string assemblyPath,
        string assemblyName,
        FileStream stream,
        PluginManifest manifest,
        bool bundled)
    {
        PluginLoadContext? context = null;
        var handedOver = false;

        try
        {
            // 7. Load from the handle that has denied writers since before the hash was taken.
            //    The constructor can throw on a corrupt .deps.json, which is why it is in here.
            context = new PluginLoadContext(assemblyPath, $"Plugin:{name}");
            stream.Seek(0, SeekOrigin.Begin);
            var assembly = context.LoadFromStream(stream);

            // 8. Discovery is one declared entry point, so no arbitrary constructor runs while the
            //    host works out what this assembly is.
            var attribute = assembly.GetCustomAttribute<AudioSourceProviderAttribute>();
            if (attribute is null)
            {
                return new PluginLoadResult(
                    name,
                    PluginLoadStatus.Failed,
                    $"'{assemblyName}' has no assembly-level AudioSourceProvider attribute, so it declares no provider.",
                    manifest,
                    isBundled: bundled);
            }

            // 9. Construct the one declared type.
            if (!TryCreateProvider(attribute.ProviderType, out var provider, out var failure))
            {
                return new PluginLoadResult(name, PluginLoadStatus.Failed, failure, manifest, isBundled: bundled);
            }

            handedOver = true;
            return new PluginLoadResult(
                name,
                PluginLoadStatus.Loaded,
                bundled ? "Loaded. Trusted without approval: these are the files shipped with Dongled." : "Loaded.",
                manifest,
                new LoadedPlugin(name, provider!, context.Unload),
                bundled);
        }
        catch (Exception ex)
        {
            // Past the trust decision, so this is the plugin being broken rather than the host
            // being unable to decide. Failed says that; Blocked would not.
            _logger.LogError(ex, "Plugin {Directory} was trusted but could not be used.", name);

            return new PluginLoadResult(
                name,
                PluginLoadStatus.Failed,
                $"'{assemblyName}' could not be loaded: {ex.Message}",
                manifest,
                isBundled: bundled);
        }
        finally
        {
            if (!handedOver)
            {
                // Nothing else will ever ask for this context, and leaving it alive would hold the
                // plugin's dependencies loaded for the life of the process.
                context?.Unload();
            }
        }
    }

    private void Report(PluginLoadResult result)
    {
        switch (result.Status)
        {
            case PluginLoadStatus.Failed:
            case PluginLoadStatus.Blocked:
            case PluginLoadStatus.Changed:
                _logger.LogWarning(
                    "Plugin {Directory} was not loaded: {Message}",
                    result.Directory,
                    result.Message);
                break;

            // The message is written for the Plugins page, where it sits beside a status. For a
            // loaded plugin it says nothing the status does not, so the log line leaves it out.
            case PluginLoadStatus.Loaded:
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    if (result.IsBundled)
                    {
                        _logger.LogInformation(
                            "Plugin {Directory} loaded, trusted as shipped with Dongled.",
                            result.Directory);
                    }
                    else
                    {
                        _logger.LogInformation("Plugin {Directory} loaded.", result.Directory);
                    }
                }

                break;

            default:
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Plugin {Directory} was not loaded ({Status}): {Message}",
                        result.Directory,
                        result.Status,
                        result.Message);
                }

                break;
        }
    }
}
