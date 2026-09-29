using System.Globalization;
using Dongled.Core.Updates;

namespace Dongled.App.Presentation;

/// <summary>The sentences the pages use to describe update state.</summary>
internal static class UpdateText
{
    /// <summary>The banner's title.</summary>
    public static string Available(string version) => $"Dongled {version} is available";

    /// <summary>The line on the Settings page.</summary>
    /// <param name="snapshot">The current state.</param>
    /// <param name="checkOnOpen">Whether the setting to check on open is on.</param>
    /// <param name="progress">The fraction downloaded, or null when nothing is.</param>
    public static string Status(UpdateSnapshot snapshot, bool checkOnOpen, double? progress)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.IsInstalling)
        {
            return progress is { } fraction
                ? string.Create(CultureInfo.CurrentCulture, $"Downloading the update… {fraction:P0}")
                : "Downloading the update…";
        }

        return snapshot.Status switch
        {
            UpdateCheckStatus.Checking => "Checking for updates…",
            UpdateCheckStatus.UpdateAvailable when snapshot.AvailableVersion is { } version =>
                $"{Available(version)}.{Checked(snapshot)}",
            UpdateCheckStatus.UpToDate => $"Dongled is up to date.{Checked(snapshot)}",
            UpdateCheckStatus.Failed => $"Could not check for updates. {snapshot.Problem}",
            _ => checkOnOpen
                ? "Not checked yet. Dongled checks when you open this window."
                : "Not checked yet.",
        };
    }

    private static string Checked(UpdateSnapshot snapshot) =>
        snapshot.LastCheckedUtc is { } at
            ? string.Create(CultureInfo.CurrentCulture, $" Last checked {at.ToLocalTime():g}.")
            : string.Empty;
}
