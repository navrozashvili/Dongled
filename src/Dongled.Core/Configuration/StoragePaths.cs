using System.IO;

namespace Dongled.Core.Configuration;

/// <summary>
/// Where the app keeps its files. Per user; nothing is ever written machine wide.
/// </summary>
public static class StoragePaths
{
    /// <summary>
    /// Environment variable that moves every writable path to a different folder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lets a development build run against a scratch folder, so it can never overwrite the
    /// configuration of the copy of the app you actually use.
    /// </para>
    /// <para>
    /// Read once, when this class is first touched. Setting it after the app has started changes
    /// nothing, which is deliberate: a path that could move mid-session would let two components
    /// disagree about where the state file is.
    /// </para>
    /// <para>
    /// A blank or whitespace value is ignored rather than treated as the current directory, so
    /// exporting it empty cannot silently scatter configuration into whatever folder the app
    /// happened to start in.
    /// </para>
    /// </remarks>
    public const string DataDirectoryVariable = "DONGLED_DATA_DIR";

    /// <summary>
    /// Root folder under the current user's roaming application data, or whatever
    /// <see cref="DataDirectoryVariable"/> names.
    /// </summary>
    public static string AppDataDirectory { get; } = ResolveAppDataDirectory();

    /// <summary>User settings and rules. Written only by the UI.</summary>
    public static string ConfigFile { get; } = Path.Combine(AppDataDirectory, "config.json");

    /// <summary>Captured previous defaults. Written only by the switching engine.</summary>
    public static string StateFile { get; } = Path.Combine(AppDataDirectory, "state.json");

    /// <summary>
    /// Where the window was last put away. Written only by the UI.
    /// </summary>
    /// <remarks>
    /// A third file rather than a corner of one of the other two, so that the one-writer-per-file
    /// rule holds and a page holding a loaded <see cref="AppConfig"/> cannot revert it. See
    /// <see cref="WindowPlacement"/>.
    /// </remarks>
    public static string WindowFile { get; } = Path.Combine(AppDataDirectory, "window.json");

    /// <summary>Rolling log files.</summary>
    public static string LogDirectory { get; } = Path.Combine(AppDataDirectory, "logs");

    /// <summary>
    /// Where plugin directories live: one folder per plugin, beside the executable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Loaded from, and written to by the Plugins page's install and remove actions. It is the
    /// one member here that does not sit under <see cref="AppDataDirectory"/>, because a plugin has
    /// to be somewhere the load context can resolve its dependencies from.
    /// </para>
    /// <para>
    /// An app which writes where it loads code from is one bug away from loading what it wrote.
    /// That objection is answered rather than dismissed: the app writes only a package the user chose in that moment,
    /// with the user's own privileges — the same act as copying it in with Explorer — and nothing
    /// written here is loaded until the user has separately approved it in a dialog that shows the
    /// hash. Installing files and trusting files stay two acts.
    /// </para>
    /// <para>
    /// An install directory a user can write to is exactly why approval pins a hash over the whole
    /// directory rather than trusting the path. That is integrity, not containment: a plugin the
    /// user approved runs in process with their full privileges.
    /// </para>
    /// </remarks>
    public static string PluginsDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Plugins");

    /// <summary>Scratch space where plugin packages are extracted before they are installed.</summary>
    /// <remarks>
    /// Under the writable per-user root rather than inside <see cref="PluginsDirectory"/>, where the
    /// loader would list a working folder as a plugin. A move from here may cross volumes and
    /// degrade to copy-then-delete.
    /// </remarks>
    public static string StagingDirectory { get; } = Path.Combine(AppDataDirectory, "staging");

    /// <summary>
    /// Whether the writable paths have been redirected away from the real per-user folder. The
    /// Settings page says so on screen, because a build silently writing somewhere else is the kind
    /// of thing that wastes an afternoon.
    /// </summary>
    public static bool IsRedirected { get; } =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DataDirectoryVariable));

    private static string ResolveAppDataDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(DataDirectoryVariable);

        if (string.IsNullOrWhiteSpace(configured))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Dongled");
        }

        // Rooted so that every consumer agrees on the location regardless of what the working
        // directory happens to be when they ask.
        return Path.GetFullPath(configured);
    }
}
