using System.Threading.Tasks;
using Dongled.App.Services;
using Dongled.Core.Updates;

namespace Dongled.App.ViewModels;

/// <summary>
/// Installing an update after the user clicked Update: say what is about to happen, do it, and
/// restart; or say why it did not happen.
/// </summary>
/// <remarks>
/// Shared by the Status page's banner and the Settings page, so both ask the same question and
/// report a refusal the same way. Nothing here ever runs without that click.
/// </remarks>
internal sealed class UpdateFlow
{
    private readonly IUpdateService _updates;
    private readonly IDialogService _dialogs;
    private readonly Action _requestRestart;

    /// <param name="updates">Downloads and puts the release in place.</param>
    /// <param name="dialogs">Asks first, and reports a failure.</param>
    /// <param name="requestRestart">Shuts the app down and starts the copy now in the folder.</param>
    public UpdateFlow(IUpdateService updates, IDialogService dialogs, Action requestRestart)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(requestRestart);

        _updates = updates;
        _dialogs = dialogs;
        _requestRestart = requestRestart;
    }

    /// <summary>Run the whole flow once.</summary>
    /// <param name="progress">Receives the fraction downloaded.</param>
    /// <returns>Whether the update was put in place and a restart requested.</returns>
    public async Task<bool> RunAsync(IProgress<double>? progress)
    {
        var snapshot = _updates.Current;
        if (!_updates.IsSupported || snapshot.AvailableVersion is not { } version || snapshot.IsInstalling)
        {
            return false;
        }

        var confirmed = await _dialogs.ConfirmAsync(new ConfirmationRequest(
            $"Update to Dongled {version}?",
            "Dongled will download the new version from GitHub, check it against the checksum published with the release, replace its own files and restart. Your settings, rules, and any plugins you added yourself are kept.",
            "Update and restart",
            "Not now"));

        if (!confirmed)
        {
            return false;
        }

        try
        {
            await _updates.InstallAsync(progress);
        }
        catch (UpdateException ex)
        {
            await _dialogs.ShowMessageAsync("The update was not installed", ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync(
                "The update was not installed",
                $"Something unexpected went wrong, and Dongled was left as it was. {ex.Message}");
            return false;
        }

        _requestRestart();
        return true;
    }
}
