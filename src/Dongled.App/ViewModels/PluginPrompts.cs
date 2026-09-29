using Dongled.App.Services;
using Dongled.Core.Plugins;

namespace Dongled.App.ViewModels;

/// <summary>The questions the Plugins page asks before acting.</summary>
/// <remarks>
/// Kept together so that the wording is in one place. In particular, a plugin the user has just
/// installed must face the same approval dialog, with the same words, as one copied in by hand.
/// </remarks>
internal static class PluginPrompts
{
    /// <summary>
    /// What approving a plugin means. It states the limits of the protection, not only the
    /// protection, so that nobody mistakes the hash check for a sandbox.
    /// </summary>
    public const string ApprovalWarning =
        "A plugin runs inside this app, as you, with everything you can do. Approving it records a "
        + "hash of every file in its folder, so you will be asked again if any of them change — but "
        + "that is integrity checking, not a sandbox. It does not limit what the plugin can do once "
        + "you have approved it. Only enable plugins you obtained from somewhere you trust.";

    /// <summary>Ask whether to trust and switch on a plugin's folder.</summary>
    /// <param name="title">What to call the plugin.</param>
    /// <param name="directory">The folder being approved.</param>
    /// <param name="sha256">The hash approval would record, or null if the folder could not be hashed.</param>
    /// <param name="justInstalled">
    /// Whether the files were installed a moment ago. The cancel button then says "Not now" rather
    /// than "Cancel", because the install has happened either way and a "Cancel" would read as
    /// undoing it.
    /// </param>
    public static ConfirmationRequest Approve(string title, string directory, string? sha256, bool justInstalled) =>
        new($"Enable {title}?", ApprovalWarning, "Enable", justInstalled ? "Not now" : "Cancel")
        {
            Fields =
            [
                new DialogField("Folder", directory),

                // Shown so a user who was given a hash some other way can compare it. The absent
                // case is spelled out, because a blank hash reads as "nothing to check".
                new DialogField(
                    "SHA-256 of every file in that folder",
                    sha256 ?? "This folder could not be hashed, so it cannot be approved.",
                    Monospace: true),
            ],
            CanConfirm = sha256 is not null,
        };

    /// <summary>Ask whether to switch a plugin off.</summary>
    /// <param name="title">What to call the plugin.</param>
    public static ConfirmationRequest Disable(string title) =>
        new(
            $"Switch off {title}?",
            "It keeps running until you restart the app. Its approval is kept, so switching it back "
                + "on will not ask you to approve the same files again.",
            "Switch off",
            "Cancel");

    /// <summary>Ask whether to delete a plugin's folder.</summary>
    /// <param name="title">What to call the plugin.</param>
    public static ConfirmationRequest Remove(string title) =>
        new(
            $"Remove {title}?",
            "Its folder is deleted and its approval is forgotten. This is not the same as switching it "
                + "off: if you put the same plugin back later, you will be asked to approve it again.",
            "Remove",
            "Cancel");

    /// <summary>Ask whether to extract a package into the plugins directory.</summary>
    /// <param name="inspection">What the package is and what it would replace.</param>
    /// <remarks>
    /// Informational rather than a consent gate: extracting files is reversible and loads nothing.
    /// Approval is asked for separately, afterwards.
    /// </remarks>
    public static ConfirmationRequest Install(IPackageInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);

        var upgrade = inspection.Outcome == PluginInstallOutcome.Upgrade;

        DialogField[] fields = inspection.DifferingFiles.Count > 0
            ?
            [
                new DialogField("Folder", inspection.DirectoryName),
                new DialogField("Plugin", inspection.AssemblyName),

                // Only available here, where both manifests are in hand. Configuration holds a
                // single combined hash, so once installed there is nothing to compare against.
                new DialogField(
                    "Files that differ from the installed copy",
                    string.Join(Environment.NewLine, inspection.DifferingFiles),
                    Monospace: true),
            ]
            :
            [
                new DialogField("Folder", inspection.DirectoryName),
                new DialogField("Plugin", inspection.AssemblyName),
            ];

        return new(
            upgrade ? $"Replace {inspection.DirectoryName}?" : $"Add {inspection.DirectoryName}?",
            inspection.Message,
            upgrade ? "Replace" : "Add",
            "Cancel")
        {
            Fields = fields,
        };
    }
}
