using System.Reflection;
using Dongled.Abstractions;
using Xunit;

[assembly: AudioSourceProvider(typeof(Dongled.Core.Tests.Sdk.TestProvider))]

namespace Dongled.Core.Tests.Sdk;

public class PluginSdkTests
{
    [Fact]
    public void Descriptors_with_the_same_values_are_equal()
    {
        var a = new AudioSourceDescriptor("hyperx:cloud-iii-s-wireless:any", "HyperX Cloud III S Wireless", "any headset paired to the dongle");
        var b = new AudioSourceDescriptor("hyperx:cloud-iii-s-wireless:any", "HyperX Cloud III S Wireless", "any headset paired to the dongle");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Descriptors_differing_only_by_display_name_are_not_equal()
    {
        var a = new AudioSourceDescriptor("id", "Old name", null);
        var b = new AudioSourceDescriptor("id", "New name", null);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Unknown_is_the_default_presence()
    {
        // Unknown must be representable AND the zero value, so a source
        // nobody has reported on reads as Unknown rather than Absent.
        Assert.Equal(Presence.Unknown, default(Presence));
    }

    [Fact]
    public void Provider_log_levels_match_the_Microsoft_Extensions_Logging_values()
    {
        // Abstractions must not reference Microsoft.Extensions.Logging,
        // but the host maps config's LogLevel onto this enum. Matching the numeric
        // values makes that mapping a cast and keeps it lossless.
        Assert.Equal(0, (int)ProviderLogLevel.Trace);
        Assert.Equal(1, (int)ProviderLogLevel.Debug);
        Assert.Equal(2, (int)ProviderLogLevel.Information);
        Assert.Equal(3, (int)ProviderLogLevel.Warning);
        Assert.Equal(4, (int)ProviderLogLevel.Error);
        Assert.Equal(5, (int)ProviderLogLevel.Critical);
        Assert.Equal(6, (int)ProviderLogLevel.None);
    }

    [Fact]
    public void The_entry_point_attribute_round_trips_through_assembly_metadata()
    {
        // The loader reads this from the plugin assembly rather than scanning
        // types, so exactly one declared entry point exists and no arbitrary
        // constructor runs during discovery.
        var assembly = typeof(PluginSdkTests).Assembly;

        var attribute = assembly.GetCustomAttribute<AudioSourceProviderAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(typeof(TestProvider), attribute.ProviderType);

        // Construct it the way the loader will. This asserts the three things the
        // attribute's doc comment promises but nothing else checks: the declared type is
        // concrete, it implements IAudioSourceProvider, and it has a public parameterless
        // constructor.
        var provider = (IAudioSourceProvider)Activator.CreateInstance(attribute.ProviderType)!;

        Assert.Equal("test.provider", provider.Metadata.Id);
    }

    [Fact]
    public void The_attribute_targets_assemblies_only_and_forbids_duplicates()
    {
        var usage = typeof(AudioSourceProviderAttribute).GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Assembly, usage.ValidOn);
        Assert.False(usage.AllowMultiple);
    }

    [Fact]
    public void Charge_state_treats_unknown_as_its_zero_value()
    {
        // Same reason Presence does: a state nobody reported must not read as a definite one.
        Assert.Equal(ChargeState.Unknown, default(ChargeState));
    }

    [Fact]
    public void Charge_state_distinguishes_full_from_charging()
    {
        // The Logitech agent reports fullyCharged separately from charging, and a device sitting on
        // a dock at 100% is not the same thing as one filling up.
        Assert.NotEqual(ChargeState.Charging, ChargeState.Full);
    }

    [Fact]
    public void The_sdk_assembly_stays_at_version_1_0()
    {
        // Nothing was ever published against 1.0, so battery folds into it rather than becoming 1.1.
        var version = typeof(IAudioSourceProvider).Assembly.GetName().Version!;
        Assert.Equal(1, version.Major);
        Assert.Equal(0, version.Minor);
    }
}

/// <summary>Minimal provider used to prove the SDK contract compiles and the attribute resolves.</summary>
internal sealed class TestProvider : IAudioSourceProvider
{
    public ProviderMetadata Metadata { get; } = new("test.provider", "Test Provider", null, IsExperimental: false);

    public Task StartAsync(IProviderContext context, CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
