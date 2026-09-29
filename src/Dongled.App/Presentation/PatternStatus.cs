using System.Collections.Generic;
using System.Linq;
using Dongled.Core.Audio;

namespace Dongled.App.Presentation;

/// <summary>
/// The line under a name pattern in the rules editor, saying what it matches right now.
/// </summary>
/// <param name="Text">What to show. Names only, never identifiers.</param>
/// <param name="IsProblem">Whether the pattern, as it stands, would not do what the user probably means.</param>
internal sealed record PatternStatus(string Text, bool IsProblem)
{
    /// <summary>Describe what <paramref name="pattern"/> matches among <paramref name="endpoints"/>.</summary>
    /// <param name="pattern">The pattern as typed.</param>
    /// <param name="preferredId">The device picked in the dropdown, which wins among several matches.</param>
    /// <param name="endpoints">The current enumeration.</param>
    public static PatternStatus For(string pattern, string? preferredId, IReadOnlyList<AudioEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(endpoints);

        if (string.IsNullOrWhiteSpace(pattern))
        {
            return new("Type a pattern, or pick a device above to fill one in.", true);
        }

        if (!DeviceNameMatcher.TryValidate(pattern, out var error))
        {
            return new($"This pattern can't be used: {error}", true);
        }

        var candidates = DeviceNameMatcher.Candidates(pattern, endpoints);
        var chosen = DeviceNameMatcher.Pick(candidates, preferredId);

        if (chosen is null)
        {
            return new("Nothing connected matches this right now.", true);
        }

        if (candidates.Count == 1)
        {
            return new($"Matches {chosen.DisplayName}.", false);
        }

        var names = string.Join(", ", candidates.Select(static endpoint => endpoint.DisplayName));
        return new(
            $"Matches {candidates.Count} devices: {names}. The rule will use {chosen.DisplayName}. Make the pattern more specific to choose.",
            true);
    }
}
