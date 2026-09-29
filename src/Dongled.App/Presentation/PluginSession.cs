using System.Collections.Generic;

namespace Dongled.App.Presentation;

/// <summary>
/// What has happened to the plugins directory since this app started, which is everything the
/// Plugins page knows that the loader does not.
/// </summary>
/// <remarks>
/// <para>
/// None of it can come from <see cref="Core.Plugins.PluginHost.Results"/>. That is a photograph of
/// the plugins directory taken once, while loading ran, because loading is deliberately
/// startup-only: a folder installed a minute ago is not in it, a folder deleted a minute ago still
/// is, and a folder replaced a minute ago is described by files that are no longer there.
/// </para>
/// <para>
/// Here rather than inside the view model, and pure, so that the rule holding it together can be
/// tested without a XAML runtime — the arrangement <see cref="PluginRows"/> and
/// <see cref="BatteryOrdering"/> use. That rule is <see cref="PluginRows.Order"/>'s caller
/// contract: <see cref="Installed"/> and <see cref="Removed"/> must be mutually exclusive, because
/// sets carry no order and a name in both cannot be read as either sequence. Every method here
/// that adds a name to one takes it out of the other, which is a thing to get right once rather
/// than at each call site.
/// </para>
/// <para>
/// Registered as a singleton, in <c>App.BuildHost</c>, and that is load-bearing rather than tidy.
/// The Plugins page builds a fresh view model on every navigation, so anything held on the view
/// model lasts until the user clicks another nav item: a plugin removed a moment ago would be
/// listed again on the way back, offering to approve the hash of files that are no longer there.
/// The lifetime this describes has to be the process's, because the startup photograph it is
/// correcting lasts that long too.
/// </para>
/// <para>
/// Not synchronised, and it does not need to be: every caller is a view model method reached from a
/// UI event, on the one thread.
/// </para>
/// </remarks>
internal sealed class PluginSession
{
    // Ordinal-ignore-case throughout, for the reason PluginRows.Order uses it: these are directory
    // names, and the file system that produced them does not distinguish "Sample" from "SAMPLE".
    // Comparing them any other way would let one folder occupy two rows.
    private readonly Dictionary<string, string> _installed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether something has been done that only a restart applies, so the page can offer one.
    /// </summary>
    /// <remarks>
    /// Here rather than on the view model for the reason the two collections are: approving a plugin
    /// and then looking at another page would otherwise lose the offer, while the row went on saying
    /// the plugin loads on the next start. The thing that needs restarting is the process, so the
    /// flag saying so lasts as long as the process.
    /// </remarks>
    public bool NeedsRestart { get; private set; }

    /// <summary>Remember that only a restart will apply what was just done.</summary>
    /// <remarks>
    /// One way only. Nothing clears it, because nothing short of restarting undoes the reason it was
    /// set: a plugin approved and then switched off again has still had its configuration changed
    /// twice since the loader last read it.
    /// </remarks>
    public void RecordNeedsRestart() => NeedsRestart = true;

    /// <summary>The directories installed since startup, for <see cref="PluginRows.Order"/>.</summary>
    public IReadOnlyList<string> Installed => [.. _installed.Keys];

    /// <summary>The directories removed since startup, for <see cref="PluginRows.Order"/>.</summary>
    public IReadOnlyList<string> Removed => [.. _removed];

    /// <summary>Remember that a directory was installed, and with which hash.</summary>
    /// <param name="directory">The folder the installer wrote.</param>
    /// <param name="sha256">
    /// The hash the installer recomputed at the destination, which is what approval records and
    /// what the row shows. Not the package's, though the install only succeeded because the two
    /// matched.
    /// </param>
    public void RecordInstalled(string directory, string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);

        _installed[directory] = sha256;
        _removed.Remove(directory);
    }

    /// <summary>Remember that a directory was removed.</summary>
    /// <param name="directory">The folder that is gone.</param>
    public void RecordRemoved(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _removed.Add(directory);
        _installed.Remove(directory);
    }

    /// <summary>
    /// The hash the installer computed at the destination, or <see langword="null"/> if this
    /// session did not install this directory.
    /// </summary>
    /// <param name="directory">The folder to ask about.</param>
    /// <remarks>
    /// A non-null answer is also what tells the page that the load result it holds for this
    /// directory describes files that have since been replaced.
    /// </remarks>
    public string? InstalledSha256Of(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        return _installed.GetValueOrDefault(directory);
    }
}
