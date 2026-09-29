using Dongled.App.Presentation;
using Dongled.Core.Audio;
using Xunit;

namespace Dongled.App.Tests.Presentation;

public class PatternStatusTests
{
    private static AudioEndpoint Active(string id, string name) => new(id, name, true, false, false);

    private static readonly AudioEndpoint[] Endpoints =
    [
        Active("{0.0.0.00000000}.{usb}", "Speakers (USB DAC)"),
        Active("{0.0.0.00000000}.{realtek}", "Speakers (Realtek)"),
        Active("{0.0.0.00000000}.{headset}", "Headset (HyperX Cloud III S)"),
        new("{0.0.0.00000000}.{dock}", "Speakers (Dock)", false, false, false),
    ];

    [Fact]
    public void One_match_is_named()
    {
        var status = PatternStatus.For("hyperx", null, Endpoints);

        Assert.False(status.IsProblem);
        Assert.Equal("Matches Headset (HyperX Cloud III S).", status.Text);
    }

    [Fact]
    public void Several_matches_are_listed_with_the_one_the_rule_will_use()
    {
        var status = PatternStatus.For("^Speakers", "{0.0.0.00000000}.{usb}", Endpoints);

        Assert.True(status.IsProblem);
        Assert.Equal(
            "Matches 2 devices: Speakers (Realtek), Speakers (USB DAC). The rule will use Speakers (USB DAC). Make the pattern more specific to choose.",
            status.Text);
    }

    [Fact]
    public void No_connected_match_says_so()
    {
        var status = PatternStatus.For("dock", null, Endpoints);

        Assert.True(status.IsProblem);
        Assert.Equal("Nothing connected matches this right now.", status.Text);
    }

    [Fact]
    public void An_unusable_pattern_says_why()
    {
        var status = PatternStatus.For("(", null, Endpoints);

        Assert.True(status.IsProblem);
        Assert.StartsWith("This pattern can't be used: ", status.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_pattern_asks_for_one()
    {
        var status = PatternStatus.For("  ", null, Endpoints);

        Assert.True(status.IsProblem);
        Assert.Equal("Type a pattern, or pick a device above to fill one in.", status.Text);
    }

    [Fact]
    public void No_status_text_contains_an_endpoint_identifier()
    {
        foreach (var pattern in new[] { "hyperx", "^Speakers", "dock", "(", "" })
        {
            Assert.False(ActivityComposer.ContainsEndpointIdentifier(PatternStatus.For(pattern, null, Endpoints).Text));
        }
    }
}
