using System.Linq;
using Dongled.Core.Audio;
using Dongled.Core.Configuration;
using Dongled.Core.Interop;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dongled.Core.Tests.Audio;

/// <summary>
/// Exercises the real service against whatever audio the machine has, including none. A CI
/// runner has no endpoints, so every assertion holds for an empty result too; anything that
/// needed a specific device would be a test that only ever runs on one machine.
/// </summary>
/// <remarks>
/// Nothing here changes a default endpoint. Switching the machine's audio output as a side
/// effect of running the test suite is not acceptable, so the switching path is verified by the
/// policy tests against the fake, and by the manual smoke test in the final task.
/// </remarks>
public class CoreAudioEndpointServiceTests
{
    private static CoreAudioEndpointService Create() => new(NullLogger<CoreAudioEndpointService>.Instance);

    [Fact]
    public void Enumeration_reports_every_endpoint_with_a_usable_identifier()
    {
        using var service = Create();

        var endpoints = service.Enumerate();

        Assert.All(endpoints, endpoint => Assert.False(string.IsNullOrWhiteSpace(endpoint.Id)));
        Assert.Equal(
            endpoints.Select(endpoint => endpoint.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            endpoints.Count);
    }

    [Fact]
    public void Enumeration_agrees_with_the_reported_defaults()
    {
        using var service = Create();

        var defaults = service.GetDefaults();
        var endpoints = service.Enumerate();

        foreach (var endpoint in endpoints)
        {
            Assert.Equal(
                string.Equals(endpoint.Id, defaults.MultimediaId, StringComparison.OrdinalIgnoreCase),
                endpoint.IsDefaultMultimedia);
            Assert.Equal(
                string.Equals(endpoint.Id, defaults.CommunicationsId, StringComparison.OrdinalIgnoreCase),
                endpoint.IsDefaultCommunications);
        }
    }

    [Fact]
    public void Enumeration_puts_the_endpoints_holding_a_role_first_then_the_active_ones()
    {
        using var service = Create();

        var endpoints = service.Enumerate();

        var ranks = endpoints
            .Select(endpoint => endpoint.IsDefaultMultimedia || endpoint.IsDefaultCommunications ? 0
                : endpoint.IsActive ? 1
                : 2)
            .ToArray();

        Assert.Equal(ranks.OrderBy(rank => rank).ToArray(), ranks);
    }

    [Fact]
    public void Enumeration_releases_every_wrapper_it_created()
    {
        using var service = Create();

        // Counting the releases, rather than watching memory: omitting every release makes no difference to private bytes or handle count even over
        // thousands of enumerations, because the wrappers are finalizable. A memory-based
        // assertion here would pass whether or not anything was released.
        var endpoints = service.Enumerate();
        var defaults = service.GetDefaults();

        var expected = 2 // the enumerator and the collection
            + (defaults.MultimediaId is null ? 0 : 1)
            + (defaults.CommunicationsId is null ? 0 : 1)
            + endpoints.Count // one wrapper per endpoint
            + endpoints.Count(endpoint => endpoint.DisplayName.Length > 0); // and its property store

        var before = Com.ReleaseCount;
        service.Enumerate();
        var released = Com.ReleaseCount - before;

        // A lower bound rather than equality only because a property store that opens and then
        // refuses to give up a name is still released while contributing no name to count by. Any
        // release removed from the implementation drops this below the bound.
        Assert.True(
            released >= expected,
            $"expected at least {expected} wrappers to be released by one enumeration, but {released} were");
    }

    [Fact]
    public void Repeated_enumeration_stays_consistent()
    {
        using var service = Create();

        var first = service.Enumerate().Count;
        for (var i = 0; i < 99; i++)
        {
            Assert.Equal(first, service.Enumerate().Count);
        }
    }

    [Fact]
    public void An_endpoint_identifier_that_does_not_resolve_is_refused_rather_than_switched_to()
    {
        using var service = Create();

        var applied = service.SetDefault(
            "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}",
            [AudioRole.Media]);

        Assert.False(applied);
    }

    [Fact]
    public void Setting_no_roles_changes_nothing_and_reports_success()
    {
        using var service = Create();
        var before = service.GetDefaults();

        var applied = service.SetDefault("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}", []);

        Assert.True(applied);
        Assert.Equal(before, service.GetDefaults());
    }

    [Fact]
    public void A_blank_endpoint_identifier_is_a_caller_bug_rather_than_a_refusal()
    {
        using var service = Create();

        Assert.Throws<ArgumentException>(() => service.SetDefault("   ", [AudioRole.Media]));
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        var service = Create();

        service.Dispose();
        service.Dispose();
    }
}
