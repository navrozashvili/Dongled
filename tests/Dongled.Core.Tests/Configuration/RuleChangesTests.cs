using Dongled.Core.Configuration;
using Xunit;

namespace Dongled.Core.Tests.Configuration;

public class RuleChangesTests
{
    private static Rule Headset() => new()
    {
        Id = "r1",
        Name = "HyperX",
        Enabled = true,
        Source = new RuleSource { Id = "hyperx:any", LastKnownName = "HyperX Cloud III S Wireless" },
        Target = new RuleTarget
        {
            DeviceId = "{0.0.0.00000000}.{headset}",
            LastKnownName = "Headset (HyperX)",
            Roles = [AudioRole.Media, AudioRole.Calls],
        },
        OnDisconnect = new DisconnectBehavior
        {
            Mode = DisconnectMode.AlwaysFallback,
            FallbackDeviceId = "{0.0.0.00000000}.{edifier}",
            FallbackLastKnownName = "Headphones (EDIFIER R1280DBs)",
        },
    };

    [Fact]
    public void Identical_rules_describe_no_change()
    {
        Assert.Empty(RuleChanges.Describe([Headset()], [Headset()]));
    }

    [Fact]
    public void A_retargeted_rule_names_both_devices_with_their_identifiers()
    {
        var after = Headset();
        after.Target.DeviceId = "{0.0.0.00000000}.{edifier}";
        after.Target.LastKnownName = "Headphones (EDIFIER R1280DBs)";

        var change = Assert.Single(RuleChanges.Describe([Headset()], [after]));

        Assert.Equal(
            "Rule HyperX: target changed from Headset (HyperX) [{0.0.0.00000000}.{headset}] to Headphones (EDIFIER R1280DBs) [{0.0.0.00000000}.{edifier}].",
            change);
    }

    [Fact]
    public void Every_changed_field_is_described()
    {
        var after = Headset();
        after.Name = "Headset rule";
        after.Enabled = false;
        after.Source = new RuleSource { Id = "logitech:g733", LastKnownName = "G733" };
        after.Target.NamePattern = "^Headset";
        after.Target.Roles = [AudioRole.Media];
        after.OnDisconnect.Mode = DisconnectMode.RestorePrevious;
        after.OnDisconnect.FallbackDeviceId = null;
        after.OnDisconnect.FallbackLastKnownName = null;
        after.OnDisconnect.FallbackNamePattern = "^Speakers";

        var changes = RuleChanges.Describe([Headset()], [after]);

        Assert.Equal(
            [
                "Rule HyperX: renamed to Headset rule.",
                "Rule Headset rule: switched off.",
                "Rule Headset rule: source changed from HyperX Cloud III S Wireless [hyperx:any] to G733 [logitech:g733].",
                "Rule Headset rule: target name pattern changed from (none) to ^Headset.",
                "Rule Headset rule: roles changed from Media and Calls to Media.",
                "Rule Headset rule: disconnect behaviour changed from AlwaysFallback to RestorePrevious.",
                "Rule Headset rule: fallback changed from Headphones (EDIFIER R1280DBs) [{0.0.0.00000000}.{edifier}] to (none).",
                "Rule Headset rule: fallback name pattern changed from (none) to ^Speakers.",
            ],
            changes);
    }

    [Fact]
    public void Added_and_removed_rules_are_described()
    {
        var added = Headset();
        added.Id = "r2";
        added.Name = "New rule";

        var changes = RuleChanges.Describe([Headset()], [added]);

        Assert.Equal(["Rule HyperX: removed.", "Rule New rule: added."], changes);
    }

    [Fact]
    public void An_unnamed_device_is_still_identified()
    {
        var after = Headset();
        after.Target.DeviceId = "{0.0.0.00000000}.{mystery}";
        after.Target.LastKnownName = null;

        Assert.Equal(
            "Rule HyperX: target changed from Headset (HyperX) [{0.0.0.00000000}.{headset}] to (unnamed) [{0.0.0.00000000}.{mystery}].",
            Assert.Single(RuleChanges.Describe([Headset()], [after])));
    }
}
