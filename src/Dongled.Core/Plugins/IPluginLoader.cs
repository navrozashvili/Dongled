using Dongled.Core.Configuration;

namespace Dongled.Core.Plugins;

/// <summary>Decides which plugins may be loaded, and loads the ones that may.</summary>
public interface IPluginLoader
{
    /// <summary>
    /// Walk a plugins root and decide about every directory under it.
    /// </summary>
    /// <param name="pluginsDirectory">
    /// The root, normally <see cref="StoragePaths.PluginsDirectory"/>. A root that does not exist
    /// is not an error; it yields no results.
    /// </param>
    /// <param name="trust">
    /// The configuration's plugin entries. A directory with no entry has never been approved and
    /// is not loaded: absence means blocked, which is why the default is nothing rather than
    /// everything. The one exception is a directory holding exactly the files this build shipped
    /// under that name (see <see cref="BundledPluginTrust"/>), which loads without an entry unless
    /// an entry switches it off.
    /// </param>
    /// <returns>
    /// One result per directory, ordered by directory name, including the directories that turned
    /// out not to be plugins. Every outcome is reported so the UI can show all of them.
    /// </returns>
    /// <remarks>
    /// This method does not throw for anything a plugin directory can do. A failure while deciding
    /// whether a plugin is permitted produces <see cref="PluginLoadStatus.Blocked"/>, never a load.
    /// </remarks>
    IReadOnlyList<PluginLoadResult> LoadAll(string pluginsDirectory, IReadOnlyList<PluginConfig> trust);
}
