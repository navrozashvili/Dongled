using System.Collections.Generic;
using System.Linq;
using Dongled.App.Presentation;
using Dongled.App.Services;
using Dongled.Core.Configuration;
using Dongled.Core.Pipeline;
using Dongled.Core.Plugins;
using Microsoft.Extensions.Logging;

namespace Dongled.App.ViewModels;

/// <summary>
/// Turns what the loader found at startup, corrected by what this session has installed and
/// removed since, into the Plugins page's rows.
/// </summary>
internal static class PluginRowBuilder
{
    /// <summary>Build one row per directory the page should list, in display order.</summary>
    /// <param name="listings">What the loader found at startup.</param>
    /// <param name="stateOf">Where each loaded directory's provider is in its lifecycle.</param>
    /// <param name="session">What has been installed and removed since startup.</param>
    /// <param name="config">Configuration, for each directory's approval and log level.</param>
    /// <param name="commands">The actions every row offers.</param>
    /// <param name="bundled">
    /// The plugins this build shipped, for directories installed since startup, which have no load
    /// result to say so. <see cref="BundledPluginTrust.Embedded"/> when not given.
    /// </param>
    public static List<PluginRow> Build(
        IReadOnlyList<PluginListing> listings,
        Func<string, ProviderRunState?> stateOf,
        PluginSession session,
        AppConfig config,
        PluginRowCommands commands,
        BundledPluginTrust? bundled = null)
    {
        ArgumentNullException.ThrowIfNull(listings);
        ArgumentNullException.ThrowIfNull(stateOf);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(commands);

        bundled ??= BundledPluginTrust.Embedded;

        var byDirectory = new Dictionary<string, PluginListing>(StringComparer.OrdinalIgnoreCase);
        var fromStartup = new List<string>(listings.Count);

        foreach (var listing in listings)
        {
            byDirectory[listing.Directory] = listing;
            fromStartup.Add(listing.Directory);
        }

        var rows = new List<PluginRow>();

        foreach (var directory in PluginRows.Order(fromStartup, session.Installed, session.Removed))
        {
            var configured = config.Plugins.FirstOrDefault(
                plugin => string.Equals(plugin.Directory, directory, StringComparison.OrdinalIgnoreCase));

            if (session.InstalledSha256Of(directory) is { } installedSha)
            {
                rows.Add(InstalledThisSession(
                    directory,
                    installedSha,
                    configured,
                    bundled.Covers(directory, installedSha),
                    commands));
                continue;
            }

            // Every name not installed this session came from the startup listings.
            rows.Add(FromStartup(byDirectory[directory], stateOf(directory), configured, commands));
        }

        return rows;
    }

    private static PluginRow FromStartup(
        PluginListing listing,
        ProviderRunState? state,
        PluginConfig? configured,
        PluginRowCommands commands)
    {
        var metadata = listing.Metadata;

        return new PluginRow(
            listing.Directory,
            string.IsNullOrWhiteSpace(metadata?.DisplayName) ? listing.Directory : metadata.DisplayName,
            metadata?.Description ?? string.Empty,
            StatusTextFor(listing.Status, state),
            MessageFor(listing),
            listing.Sha256,
            metadata?.IsExperimental ?? false,
            canApprove: listing.Status is PluginLoadStatus.Unapproved or PluginLoadStatus.Changed or PluginLoadStatus.Disabled,

            // A shipped plugin with no entry is on without ever having been approved, so it can be
            // switched off too. Switching it off writes the entry that keeps it off.
            canDisable: configured?.Enabled == true || (listing.IsBundled && configured is null),
            configured?.LogLevel ?? LogLevel.Warning,
            commands,
            listing.IsBundled);
    }

    /// <summary>A row for a directory this session installed, described without a load result.</summary>
    /// <remarks>
    /// <para>
    /// Used even when the loader saw a folder of this name at startup, because that result
    /// describes files that have since been replaced. A folder that was "Not a plugin" at startup
    /// and has just had a real plugin installed over it would otherwise offer no way to approve
    /// the new files. The cost is that the row shows the folder name rather than a display name
    /// until the next start.
    /// </para>
    /// <para>
    /// Approval is read from configuration rather than remembered, so that switching the plugin
    /// off afterwards is reflected here too.
    /// </para>
    /// </remarks>
    private static PluginRow InstalledThisSession(
        string directory,
        string sha256,
        PluginConfig? configured,
        bool isBundled,
        PluginRowCommands commands)
    {
        var state = PluginApproval.StateOf(sha256, configured?.Enabled == true, configured?.ManifestSha256);

        // The loader's rule: the files this build shipped need no approval, unless an entry
        // switches them off.
        if (isBundled && configured?.Enabled != false)
        {
            state = PluginApprovalState.Approved;
        }

        return new PluginRow(
            directory,
            directory,
            string.Empty,
            state switch
            {
                PluginApprovalState.Approved => "Installed — loads on next start",
                PluginApprovalState.SwitchedOff => "Disabled",
                _ => "Blocked — new",
            },
            state switch
            {
                PluginApprovalState.Approved => "Installed and approved. Restart to load it.",
                PluginApprovalState.SwitchedOff =>
                    "Installed and approved, then switched off. Switch it back on to let it load; the "
                        + "files have not changed, so it will not ask you to approve them again.",
                _ => "Installed, and not approved yet. Approve it to let it load.",
            },
            sha256,
            isExperimental: false,

            // The same rule the startup rows follow: anything not already approved and switched on
            // can be enabled.
            canApprove: state != PluginApprovalState.Approved,
            canDisable: state == PluginApprovalState.Approved,
            configured?.LogLevel ?? LogLevel.Warning,
            commands,
            isBundled);
    }

    private static string StatusTextFor(PluginLoadStatus status, ProviderRunState? state) => status switch
    {
        PluginLoadStatus.Loaded => state switch
        {
            ProviderRunState.Running => "Running",
            ProviderRunState.Failed => "Failed to start",
            ProviderRunState.Stopped => "Stopped",
            _ => "Starting",
        },
        PluginLoadStatus.Unapproved => "Blocked — new",
        PluginLoadStatus.Changed => "Blocked — files changed",
        PluginLoadStatus.Disabled => "Disabled",
        PluginLoadStatus.Failed => "Failed to start",
        PluginLoadStatus.Blocked => "Blocked",
        _ => "Not a plugin",
    };

    private static string MessageFor(PluginListing listing)
    {
        if (listing.IsBundled && listing.Status == PluginLoadStatus.Disabled)
        {
            return listing.Message + " Trusted: shipped with Dongled, and its files are unchanged.";
        }

        if (listing.Status != PluginLoadStatus.Changed)
        {
            return listing.Message;
        }

        // Approval records one hash for the whole folder rather than one per file, so there is
        // nothing to compare against to say which files changed. Saying so is better than implying
        // the check was less thorough than it is.
        return listing.Message
            + " Which files changed cannot be shown: approval records one hash for the whole folder, not a hash per file.";
    }
}
