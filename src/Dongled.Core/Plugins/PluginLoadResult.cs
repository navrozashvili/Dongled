using Dongled.Abstractions;

namespace Dongled.Core.Plugins;

/// <summary>What became of one plugin directory.</summary>
/// <remarks>
/// <see cref="Unapproved"/>, <see cref="Changed"/> and <see cref="Disabled"/> are separate members
/// rather than one blocked state, because the Plugins page offers a different action for each:
/// approve, approve again, or switch back on.
/// </remarks>
public enum PluginLoadStatus
{
    /// <summary>Trusted, compatible, constructed. Ready to be started.</summary>
    Loaded = 0,

    /// <summary>
    /// No candidate assembly here, or the candidate is not a managed assembly. Listed rather than
    /// reported as an error: a folder of vendor files is not a broken plugin.
    /// </summary>
    NotAPlugin = 1,

    /// <summary>New: no configuration entry names this directory, so it has never been approved.</summary>
    Unapproved = 2,

    /// <summary>Approved once, but the files have changed since. Approval does not carry over.</summary>
    Changed = 3,

    /// <summary>Approved, and the user has switched it off.</summary>
    Disabled = 4,

    /// <summary>
    /// Deciding whether this plugin was permitted failed, so it was not loaded. This is the
    /// fail-closed outcome: a failure while deciding never results in loading.
    /// </summary>
    Blocked = 5,

    /// <summary>
    /// Trusted, but not usable: built against an incompatible SDK, missing its entry-point
    /// attribute, or its declared type could not be constructed.
    /// </summary>
    Failed = 6,
}

/// <summary>A plugin that was trusted, loaded and constructed.</summary>
/// <remarks>
/// <para>
/// Disposing this asks the runtime to unload the plugin's assembly load context. It does
/// <strong>not</strong> stop the provider: that is
/// <see cref="Pipeline.ProviderRunner.StopAsync"/>'s job, and it must have completed first. The
/// whole shutdown order is: stop every provider, then close the engine's input, then dispose every
/// loaded plugin. Stopping providers last would break the promise <see cref="IProviderContext"/>
/// makes that a provider may publish and log from inside its own cleanup.
/// </para>
/// <para>
/// Unloading is a request, not an operation. The context goes away once nothing references
/// anything in it, which is why a re-enabled plugin gets a new context and a new
/// <see cref="Pipeline.ProviderRunner"/> rather than reusing either.
/// </para>
/// </remarks>
public sealed class LoadedPlugin : IDisposable
{
    private readonly Action _unload;
    private bool _disposed;

    /// <param name="directory">The directory name under the plugins root.</param>
    /// <param name="provider">The constructed provider.</param>
    /// <param name="unload">
    /// Releases the load context. A callback rather than the context itself, because the context
    /// type is internal and a test that needs a loaded plugin without a real assembly on disk
    /// would otherwise be unable to make one.
    /// </param>
    internal LoadedPlugin(string directory, IAudioSourceProvider provider, Action unload)
    {
        Directory = directory;
        Provider = provider;
        _unload = unload;
    }

    /// <summary>The directory name under the plugins root, which is also its configuration key.</summary>
    public string Directory { get; }

    /// <summary>The constructed provider. Not started.</summary>
    public IAudioSourceProvider Provider { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _unload();
    }
}

/// <summary>What the loader decided about one directory, and why.</summary>
/// <remarks>
/// One of these exists for every directory under the plugins root, including the ones that are not
/// plugins at all, because every outcome is surfaced in the UI rather than only the successes.
/// </remarks>
public sealed class PluginLoadResult
{
    internal PluginLoadResult(
        string directory,
        PluginLoadStatus status,
        string message,
        PluginManifest? manifest = null,
        LoadedPlugin? plugin = null,
        bool isBundled = false)
    {
        Directory = directory;
        Status = status;
        Message = message;
        Manifest = manifest;
        Plugin = plugin;
        IsBundled = isBundled;
    }

    /// <summary>The directory name under the plugins root.</summary>
    public string Directory { get; }

    /// <summary>What happened.</summary>
    public PluginLoadStatus Status { get; }

    /// <summary>
    /// One sentence a user can act on. Never contains an exception's stack; the log has that.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// The manifest as it is right now, when one could be computed. This is the value the UI
    /// records if the user approves, and the value it compares against the recorded one to say
    /// which files differ.
    /// </summary>
    public PluginManifest? Manifest { get; }

    /// <summary>The loaded plugin, when <see cref="Status"/> is <see cref="PluginLoadStatus.Loaded"/>.</summary>
    public LoadedPlugin? Plugin { get; }

    /// <summary>
    /// Whether these are exactly the files this build of Dongled shipped in this directory, which
    /// makes them trusted without an approval. See <see cref="BundledPluginTrust"/>. True whatever
    /// <see cref="Status"/> is, so a shipped plugin the user switched off can still be told apart.
    /// </summary>
    public bool IsBundled { get; }
}
