namespace Dongled.App.Presentation;

/// <summary>Where a plugin installed since startup stands with the user's trust.</summary>
/// <remarks>
/// Only three states are reachable, because the recorded hash and the installed one can only agree
/// or not, and an agreement can only be switched on or off. A recorded hash that disagrees is not a
/// fourth state: it will not load whatever <see cref="Core.Configuration.PluginConfig.Enabled"/> says, so
/// it is the same answer as having no hash at all.
/// </remarks>
internal enum PluginApprovalState
{
    /// <summary>Nothing on record covers these files, so nothing will load them.</summary>
    NotApproved = 0,

    /// <summary>Approved and switched on. It loads at the next start.</summary>
    Approved = 1,

    /// <summary>Approved and then switched off. Switching it back on asks nothing further.</summary>
    SwitchedOff = 2,
}

/// <summary>
/// Whether the approval recorded in <c>config.json</c> covers the files that are actually installed.
/// </summary>
/// <remarks>
/// <para>
/// Read from configuration each time rather than remembered alongside it. The Plugins page can
/// change approval by three routes — approving an installed plugin, switching one off, and
/// installing over one, which discards the approval inside
/// <see cref="Core.Plugins.PluginInstaller.Install"/> — and a second copy of the answer only has to
/// miss one of them to start telling the user something untrue.
/// </para>
/// <para>
/// Pure and separate from the view models so it can be tested without a XAML runtime, the same
/// arrangement <see cref="PluginRows"/>, <see cref="PluginSession"/> and
/// <see cref="BatteryOrdering"/> use.
/// </para>
/// </remarks>
internal static class PluginApproval
{
    /// <summary>Where an installed directory stands, given what configuration records about it.</summary>
    /// <param name="installedSha256">The hash the installer computed at the destination.</param>
    /// <param name="enabled">Whether its configuration entry is switched on.</param>
    /// <param name="recordedSha256">
    /// The hash its configuration entry records, if it has one. Absent for a directory that has
    /// never been approved, and cleared by an install that replaced the files.
    /// </param>
    public static PluginApprovalState StateOf(string installedSha256, bool enabled, string? recordedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installedSha256);

        // Deliberately the same test the loader makes, in PluginManifest.Matches, and it has to stay
        // the same test: a page that called a directory approved which the loader would then refuse
        // is a page that promises a load which never happens. A blank recorded hash never matches,
        // which is what makes an approval entry with no hash useless rather than permissive, and the
        // comparison ignores case because the value is uppercase hexadecimal that has been out to
        // config.json and back.
        var trusted = !string.IsNullOrWhiteSpace(recordedSha256)
            && string.Equals(installedSha256, recordedSha256, StringComparison.OrdinalIgnoreCase);

        if (!trusted)
        {
            return PluginApprovalState.NotApproved;
        }

        return enabled ? PluginApprovalState.Approved : PluginApprovalState.SwitchedOff;
    }
}
