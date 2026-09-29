using System.Reflection;
using System.Runtime.Loader;

namespace Dongled.Core.Plugins;

/// <summary>
/// The collectible context one plugin assembly is loaded into.
/// </summary>
/// <remarks>
/// <para>
/// Collectible so a plugin the user disables can be let go of. This is a <em>versioning</em>
/// boundary, letting two plugins carry incompatible copies of the same dependency: it is
/// <strong>not</strong> a sandbox. A plugin runs in process with the user's full privileges, and
/// nothing here contains it.
/// </para>
/// <para>
/// <c>Dongled.Abstractions</c> is pinned to the default context. A plugin's build
/// output normally contains its own copy, and loading that copy here would make the plugin's
/// <c>IAudioSourceProvider</c> a different type from the host's, so the cast after construction
/// would fail with a message about a type that appears identical.
/// </para>
/// </remarks>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private const string PinnedAssembly = "Dongled.Abstractions";

    private readonly AssemblyDependencyResolver _resolver;

    /// <param name="mainAssemblyPath">
    /// The plugin's own assembly on disk. Only its directory and <c>.deps.json</c> are used, for
    /// dependency resolution; the assembly itself is loaded from an already-open handle.
    /// </param>
    /// <param name="name">A name that identifies this plugin in a debugger and in diagnostics.</param>
    /// <exception cref="InvalidOperationException">
    /// The plugin's <c>.deps.json</c> exists but could not be parsed, or the path does not exist.
    /// Both mean the plugin cannot be loaded.
    /// </exception>
    public PluginLoadContext(string mainAssemblyPath, string name)
        : base(name, isCollectible: true) =>
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        ArgumentNullException.ThrowIfNull(assemblyName);

        if (string.Equals(assemblyName.Name, PinnedAssembly, StringComparison.OrdinalIgnoreCase))
        {
            // Null defers to the default context, which is the whole point.
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    /// <inheritdoc />
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
