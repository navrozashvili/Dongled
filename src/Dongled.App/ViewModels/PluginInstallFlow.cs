using System.Threading.Tasks;
using Dongled.App.Presentation;
using Dongled.App.Services;
using Dongled.Core.Plugins;

namespace Dongled.App.ViewModels;

/// <summary>What the add-plugin flow reports to, and asks of, the page it runs on.</summary>
internal interface IPluginInstallFlowHost
{
    /// <summary>Say that an action failed, and withdraw whatever the last action reported.</summary>
    void ReportProblem(string problem);

    /// <summary>Say what an action did, and clear any earlier problem.</summary>
    void ReportNotice(string notice);

    /// <summary>Tell everything that reads configuration that the file changed on disk.</summary>
    void NotifyConfigurationChanged();

    /// <summary>Rebuild the rows from what is on disk now.</summary>
    void Refresh();

    /// <summary>Record approval for a directory this flow has just installed.</summary>
    /// <param name="directory">The folder that was installed.</param>
    /// <param name="sha256">The hash the installer computed there.</param>
    /// <param name="title">What to call it in the notice.</param>
    void ApproveInstalled(string directory, string sha256, string title);
}

/// <summary>
/// Adding a plugin from a zip: pick a package, say what adding it would do, install it if the user
/// agrees, and then ask whether to approve it.
/// </summary>
/// <remarks>
/// <para>
/// Two questions rather than one: the first is informational, because extracting files is
/// reversible and loads nothing; the second is the same consent gate a plugin copied in by hand
/// faces.
/// </para>
/// <para>
/// Does not stop a second flow starting. The page runs this under the same one-at-a-time guard as
/// its other dialog-driven actions, because the guard has to span all of them.
/// </para>
/// </remarks>
internal sealed class PluginInstallFlow
{
    private readonly IDialogService _dialogs;
    private readonly IPluginInstaller _installer;
    private readonly PluginSession _session;
    private readonly IPluginInstallFlowHost _host;

    /// <param name="dialogs">Asks the user at each step.</param>
    /// <param name="installer">Judges a package and puts it into the plugins directory.</param>
    /// <param name="session">Where an install is recorded, so the page can list it before a restart.</param>
    /// <param name="host">The page this runs on.</param>
    public PluginInstallFlow(
        IDialogService dialogs,
        IPluginInstaller installer,
        PluginSession session,
        IPluginInstallFlowHost host)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(host);

        _dialogs = dialogs;
        _installer = installer;
        _session = session;
        _host = host;
    }

    /// <summary>Run the whole flow once.</summary>
    public async Task RunAsync()
    {
        var path = await _dialogs.PickFileAsync("Add", "Plugin package", ".zip");

        if (path is null)
        {
            return;
        }

        // The picker's filter is a convenience. What decides is the package reader, which refuses
        // anything that is not a plugin package.
        var inspected = TryInspect(path, out var inspection, out var failure);

        // Disposed on every path, including a successful install, which is safe because the
        // package checks whether its staging folder is still there before deleting it.
        using (inspection)
        {
            if (!inspected)
            {
                await _dialogs.ShowMessageAsync("That is not a plugin package", failure);
                return;
            }

            if (inspection!.Outcome is PluginInstallOutcome.Conflict or PluginInstallOutcome.AlreadyInstalled)
            {
                // Stopped here, because installing either of these throws.
                await _dialogs.ShowMessageAsync($"Cannot add {inspection.DirectoryName}", inspection.Message);
                return;
            }

            if (inspection.IsInstalledPluginLoaded)
            {
                await _dialogs.ShowMessageAsync(
                    $"Cannot replace {inspection.DirectoryName} yet",
                    $"{inspection.DirectoryName} is running, and its files are open. Restart the app, then add it again.");
                return;
            }

            if (!await _dialogs.ConfirmAsync(PluginPrompts.Install(inspection)))
            {
                return;
            }

            if (Install(inspection) is not { } landedSha256)
            {
                // Install has already reported the reason.
                return;
            }

            var directory = inspection.DirectoryName;

            if (await _dialogs.ConfirmAsync(PluginPrompts.Approve(directory, directory, landedSha256, justInstalled: true)))
            {
                // Nothing has loaded these files, so the folder is all there is to call the plugin.
                _host.ApproveInstalled(directory, landedSha256, directory);
            }
        }
    }

    /// <summary>Extract and judge an archive.</summary>
    /// <remarks>
    /// Catches everything: a zip that is malformed in an unexpected way is the user's file being
    /// wrong, not the app being broken, and should be reported as such.
    /// </remarks>
    private bool TryInspect(string zipPath, out IPackageInspection? inspection, out string failure)
    {
        try
        {
            return _installer.TryInspect(zipPath, out inspection, out failure);
        }
        catch (Exception ex)
        {
            inspection = null;
            failure = $"This file could not be read: {ex.Message}";
            return false;
        }
    }

    /// <summary>Put an inspected package into the plugins directory, and say what happened.</summary>
    /// <returns>
    /// The hash the installer computed at the destination, which is what approval must record, or
    /// null if the files are not in place.
    /// </returns>
    /// <remarks>
    /// Does not require a restart on its own: installing refuses to replace a running plugin, so
    /// nothing running has changed until the new files are approved.
    /// </remarks>
    private string? Install(IPackageInspection inspection)
    {
        var result = _installer.Install(inspection);

        if (!result.Succeeded)
        {
            _host.ReportProblem(result.Message);
            return null;
        }

        // Non-null on success: it is the hash computed at the destination.
        _session.RecordInstalled(result.DirectoryName, result.Sha256!);

        // Replacing a plugin clears its recorded approval and saves configuration, so everything
        // that reads it is told the file changed.
        _host.NotifyConfigurationChanged();

        // Reported here because neither dialog around the install says what happened, and a user
        // who answers "Not now" to the approval would otherwise be told nothing.
        _host.ReportNotice(result.Message);

        _host.Refresh();

        return result.Sha256;
    }
}
