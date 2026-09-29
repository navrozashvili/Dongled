using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Dongled.Core.Audio;

/// <summary>
/// Picks playback endpoints by a regular expression on their display name, for rules that name a
/// device by what it is called rather than by an endpoint identifier Windows may reissue.
/// </summary>
/// <remarks>
/// <para>
/// Patterns are typed by the user, so they run on the non-backtracking engine. Its cost is linear
/// in the length of the name whatever the pattern, which means no pattern can stall the switching
/// path the way <c>^(a+)+$</c> stalls a backtracking one. The price is that lookarounds,
/// backreferences and atomic groups are refused; such a pattern is reported as unusable rather
/// than silently matching nothing.
/// </para>
/// <para>
/// Building a non-backtracking regex is the expensive part, so each pattern is built once and
/// cached. The cache is small and cleared wholesale when full: the editor offers every keystroke
/// as a pattern, and nothing else here is worth an eviction policy.
/// </para>
/// </remarks>
public static class DeviceNameMatcher
{
    private const int CacheLimit = 256;

    private const RegexOptions Options =
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly ConcurrentDictionary<string, (Regex? Regex, string? Error)> Cache =
        new(StringComparer.Ordinal);

    /// <summary>A pattern that matches <paramref name="displayName"/> literally, for prefilling.</summary>
    /// <param name="displayName">The device name to start from.</param>
    public static string PatternFor(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        return Regex.Escape(displayName);
    }

    /// <summary>Whether <paramref name="pattern"/> can be used, and if not, why.</summary>
    /// <param name="pattern">The pattern as typed.</param>
    /// <param name="error">What is wrong with it, or null if nothing is.</param>
    public static bool TryValidate(string pattern, out string? error)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        (_, error) = Build(pattern);
        return error is null;
    }

    /// <summary>
    /// Whether <paramref name="displayName"/> matches <paramref name="pattern"/>. An unusable
    /// pattern matches nothing.
    /// </summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="displayName">The endpoint's display name.</param>
    public static bool IsMatch(string pattern, string displayName)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(displayName);

        var (regex, _) = Build(pattern);
        return regex is not null && regex.IsMatch(displayName);
    }

    /// <summary>
    /// Every active endpoint whose name matches, ordered by name and then by identifier so the
    /// order does not depend on which device currently holds a role.
    /// </summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="endpoints">The current enumeration.</param>
    public static IReadOnlyList<AudioEndpoint> Candidates(string pattern, IEnumerable<AudioEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(endpoints);

        var (regex, _) = Build(pattern);
        if (regex is null)
        {
            return [];
        }

        return endpoints
            .Where(endpoint => endpoint.IsActive && regex.IsMatch(endpoint.DisplayName ?? string.Empty))
            .OrderBy(static endpoint => endpoint.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static endpoint => endpoint.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The one endpoint a pattern means right now: <paramref name="preferredId"/> if it is still a
    /// candidate, so a rule keeps using the device it used last, and otherwise the first candidate.
    /// Null if there is none.
    /// </summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="preferredId">The endpoint the rule last resolved to, if any.</param>
    /// <param name="endpoints">The current enumeration.</param>
    public static AudioEndpoint? Resolve(string pattern, string? preferredId, IEnumerable<AudioEndpoint> endpoints) =>
        Pick(Candidates(pattern, endpoints), preferredId);

    /// <summary>The choice <see cref="Resolve"/> makes, for a caller that already has the candidates.</summary>
    /// <param name="candidates">What <see cref="Candidates"/> returned.</param>
    /// <param name="preferredId">The endpoint the rule last resolved to, if any.</param>
    public static AudioEndpoint? Pick(IReadOnlyList<AudioEndpoint> candidates, string? preferredId)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates.FirstOrDefault(
                endpoint => string.Equals(endpoint.Id, preferredId, StringComparison.OrdinalIgnoreCase))
            ?? (candidates.Count > 0 ? candidates[0] : null);
    }

    private static (Regex? Regex, string? Error) Build(string pattern)
    {
        if (Cache.TryGetValue(pattern, out var cached))
        {
            return cached;
        }

        (Regex? Regex, string? Error) built;
        try
        {
            built = (new Regex(pattern, Options), null);
        }
        catch (ArgumentException ex)
        {
            built = (null, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            // The non-backtracking engine refuses constructs it cannot run in linear time.
            built = (null, ex.Message);
        }

        if (Cache.Count >= CacheLimit)
        {
            Cache.Clear();
        }

        Cache[pattern] = built;
        return built;
    }
}
