using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dongled.Abstractions;
using Dongled.Core.Configuration;
using Dongled.Core.Pipeline;

namespace Dongled.App.Presentation;

/// <summary>
/// Which battery-capable sources to show, in which order, and which one the tray speaks for.
/// </summary>
/// <remarks>
/// Pure and separate from the view models so it can be tested without a XAML runtime, the same
/// arrangement <see cref="DisplayNames"/> and <see cref="ActivityComposer"/> use.
/// </remarks>
internal static class BatteryOrdering
{
    /// <summary>
    /// Every battery-capable source, in the user's order, with anything unranked following in
    /// display-name order.
    /// </summary>
    /// <remarks>
    /// A source whose battery is not currently reporting is kept, not filtered out. A wireless
    /// headset that is switched off reports nothing, and dropping it would make the one device the
    /// user most wants at the top of the list impossible to select.
    /// </remarks>
    public static IReadOnlyList<SourceState> Order(
        IReadOnlyList<SourceState> states,
        IReadOnlyList<string> preferred)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(preferred);

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < preferred.Count; index++)
        {
            // First occurrence wins, so a duplicate in a hand-edited file cannot move a source back.
            rank.TryAdd(preferred[index], index);
        }

        return states
            .Where(static state => state.Battery is not null)
            .OrderBy(state => rank.GetValueOrDefault(state.Descriptor.SourceId, int.MaxValue))
            .ThenBy(static state => state.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static state => state.Descriptor.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The order to save after the user has moved a row: everything on screen in the order it is on
    /// screen, followed by everything the saved order still names that is not.
    /// </summary>
    /// <remarks>
    /// A merge rather than a replacement, because <see cref="AppSettings.BatteryDisplayOrder"/> is
    /// documented to keep an identifier no plugin publishes any more: the user may be about to plug
    /// that device back in. Only sources currently reporting a battery are ever on screen, so a
    /// disabled plugin - or a device that has been switched off since startup and therefore never
    /// declared a battery at all - would otherwise lose its rank the moment any row was moved.
    /// </remarks>
    public static List<string> Merge(IReadOnlyList<string> shown, IReadOnlyList<string> saved)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(saved);

        var merged = new List<string>(shown);
        var onScreen = new HashSet<string>(shown, StringComparer.OrdinalIgnoreCase);

        // In their existing relative order: the user ranked them once and nothing here has any
        // reason to disturb that.
        merged.AddRange(saved.Where(id => !onScreen.Contains(id)));

        return merged;
    }

    /// <summary>
    /// The source the notification-area icon speaks for: the first in the given order that is
    /// connected and actually reporting a level, or null if none is.
    /// </summary>
    public static SourceState? PickForTray(IReadOnlyList<SourceState> ordered)
    {
        ArgumentNullException.ThrowIfNull(ordered);

        return ordered.FirstOrDefault(static state =>
            state.Presence == Presence.Present && state.Battery?.Percent is not null);
    }

    /// <summary>A phrase describing presence, in the words both the Status and Battery pages use.</summary>
    /// <remarks>
    /// Shared rather than duplicated per page, so a future wording change cannot leave the two pages
    /// describing the same device differently.
    /// </remarks>
    public static string StateFor(Presence presence) => presence switch
    {
        Presence.Present => "connected",
        Presence.Absent => "not detected",

        // Unknown is worth distinguishing here, and only here. Nothing has reported on this source
        // yet, which is a different thing from a provider having said it is absent.
        _ => "not detected yet",
    };

    /// <summary>A reading as a short phrase for a list row.</summary>
    /// <remarks>
    /// <see cref="ChargeState.Full"/> reads as "full" rather than "100% charged", because a device
    /// reporting Full may report any percentage alongside it and showing both invites the user to
    /// wonder which is lying.
    /// </remarks>
    public static string Format(BatteryReading? reading)
    {
        if (reading is null)
        {
            return "—";
        }

        if (reading.Charge == ChargeState.Full)
        {
            return "full";
        }

        if (reading.Percent is not { } percent)
        {
            return reading.Charge == ChargeState.Charging ? "charging" : "—";
        }

        var level = percent.ToString(CultureInfo.CurrentCulture) + "%";

        return reading.Charge == ChargeState.Charging ? level + " charging" : level;
    }
}
