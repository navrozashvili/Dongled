using System.Collections.Generic;
using System.Linq;

namespace Dongled.App.Presentation;

/// <summary>
/// Which plugin directories the Plugins page lists, once this session's installs and removals are
/// taken into account.
/// </summary>
/// <remarks>
/// Pure and separate from the view models so it can be tested without a XAML runtime, the same
/// arrangement <see cref="BatteryOrdering"/> and <see cref="DisplayNames"/> use.
/// </remarks>
internal static class PluginRows
{
    /// <summary>
    /// The directories to show: what the loader found at startup, less anything removed since, plus
    /// anything installed since that it could not have known about.
    /// </summary>
    /// <param name="fromStartup">
    /// The directories <see cref="Core.Plugins.PluginHost.Results"/> reported, in its order. That
    /// list is filled once, when loading runs, because loading is deliberately startup-only — so a
    /// plugin added a minute ago is not in it and a plugin deleted a minute ago still is.
    /// </param>
    /// <param name="installed">Directories installed since startup.</param>
    /// <param name="removed">Directories removed since startup.</param>
    /// <remarks>
    /// <para>
    /// A directory that was already listed keeps its position rather than moving to the end, so
    /// replacing a plugin — which is a removal and an installation of one name, and therefore the
    /// ordinary path rather than an edge case — does not reorder the list under the user.
    /// </para>
    /// <para>
    /// The caller must keep the two lists mutually exclusive: discard a name from
    /// <paramref name="removed"/> when installing it, and from <paramref name="installed"/> when
    /// removing it. Sets carry no order, so a name in both is unreadable here — "installed, then
    /// removed" and "removed, then installed" look identical — and this resolves it by asking
    /// whether the folder would still be on disk, which is what the caller knows and this does not.
    /// A name in both that startup also saw is kept; one in both that startup never saw is dropped.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Order(
        IReadOnlyList<string> fromStartup,
        IReadOnlyList<string> installed,
        IReadOnlyList<string> removed)
    {
        ArgumentNullException.ThrowIfNull(fromStartup);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(removed);

        // Ordinal-ignore-case throughout, because these are directory names and the file system
        // that produced them does not distinguish "Sample" from "SAMPLE". Comparing them any other
        // way would let one folder occupy two rows.
        var gone = new HashSet<string>(removed, StringComparer.OrdinalIgnoreCase);
        var added = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);

        // Reinstated rather than merely not-removed: a name in both lists that startup already knew
        // about is one the user replaced, and the row belongs where it has always been.
        var rows = fromStartup.Where(name => !gone.Contains(name) || added.Contains(name)).ToList();

        var listed = new HashSet<string>(rows, StringComparer.OrdinalIgnoreCase);

        // Only what is genuinely new, and only once however many times it was installed. Anything
        // still named by `removed` was installed and then removed again, so its folder is gone and
        // a row for it would offer buttons that act on nothing.
        rows.AddRange(installed.Where(name => !gone.Contains(name) && listed.Add(name)));

        return rows;
    }
}
