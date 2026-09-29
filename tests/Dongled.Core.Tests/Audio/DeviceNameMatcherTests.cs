using System.Linq;
using Dongled.Core.Audio;
using Xunit;

namespace Dongled.Core.Tests.Audio;

public class DeviceNameMatcherTests
{
    private static AudioEndpoint Active(string id, string name) => new(id, name, true, false, false);

    private static AudioEndpoint Inactive(string id, string name) => new(id, name, false, false, false);

    [Fact]
    public void An_escaped_name_matches_that_device_literally()
    {
        var pattern = DeviceNameMatcher.PatternFor("Headset (HyperX Cloud III S)");

        Assert.True(DeviceNameMatcher.IsMatch(pattern, "Headset (HyperX Cloud III S)"));
        Assert.False(DeviceNameMatcher.IsMatch(pattern, "Headset HyperX Cloud III S"));
    }

    [Fact]
    public void Matching_ignores_case()
    {
        Assert.True(DeviceNameMatcher.IsMatch("hyperx", "Headset (HyperX Cloud III S)"));
    }

    [Theory]
    [InlineData("(")]
    [InlineData("[a-")]
    [InlineData("(?<=a)b")] // Lookbehind: valid .NET syntax, refused by the non-backtracking engine.
    public void An_unusable_pattern_is_reported_and_matches_nothing(string pattern)
    {
        Assert.False(DeviceNameMatcher.TryValidate(pattern, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.False(DeviceNameMatcher.IsMatch(pattern, "a b ( [a- ab"));
    }

    [Fact]
    public void A_usable_pattern_validates()
    {
        Assert.True(DeviceNameMatcher.TryValidate("^Speakers \\(.*\\)$", out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Candidates_are_the_connected_matches_ordered_by_name()
    {
        var candidates = DeviceNameMatcher.Candidates(
            "speakers",
            [
                Active("b", "Speakers (USB DAC)"),
                Inactive("c", "Speakers (Dock)"),
                Active("a", "Speakers (Realtek)"),
                Active("d", "Headset"),
            ]);

        Assert.Equal(["Speakers (Realtek)", "Speakers (USB DAC)"], candidates.Select(e => e.DisplayName));
    }

    [Fact]
    public void Resolve_prefers_the_device_it_used_last_when_that_still_matches()
    {
        var resolved = DeviceNameMatcher.Resolve(
            "speakers",
            "b",
            [Active("a", "Speakers (Realtek)"), Active("b", "Speakers (USB DAC)")]);

        Assert.Equal("b", resolved?.Id);
    }

    [Fact]
    public void Resolve_takes_the_first_match_by_name_when_the_last_device_no_longer_matches()
    {
        // Ordered by name rather than by enumeration order, because enumeration puts whichever
        // device currently holds a role first, and the choice must not flip with the default.
        var resolved = DeviceNameMatcher.Resolve(
            "speakers",
            "gone",
            [Active("b", "Speakers (USB DAC)"), Active("a", "Speakers (Realtek)")]);

        Assert.Equal("a", resolved?.Id);
    }

    [Fact]
    public void Resolve_ignores_a_preferred_device_that_is_not_connected()
    {
        var resolved = DeviceNameMatcher.Resolve(
            "speakers",
            "c",
            [Inactive("c", "Speakers (Dock)"), Active("a", "Speakers (Realtek)")]);

        Assert.Equal("a", resolved?.Id);
    }

    [Fact]
    public void Resolve_returns_null_when_nothing_connected_matches()
    {
        Assert.Null(DeviceNameMatcher.Resolve("speakers", null, [Active("d", "Headset")]));
    }

    [Fact]
    public void A_pattern_that_would_backtrack_catastrophically_still_answers()
    {
        // The classic exponential case for a backtracking engine. The non-backtracking one is
        // linear, which is the point: a user-typed pattern must not be able to hang the engine.
        var name = new string('a', 5000) + "!";

        Assert.False(DeviceNameMatcher.IsMatch("^(a+)+$", name));
    }
}
