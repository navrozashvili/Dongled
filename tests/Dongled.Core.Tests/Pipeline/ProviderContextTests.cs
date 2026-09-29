using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dongled.Abstractions;
using Dongled.Core.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Dongled.Core.Tests.Pipeline;

/// <summary>
/// The provider context is the only thing a plugin touches, and its behaviour is documented in
/// the published SDK. Every assertion here corresponds to a sentence a plugin author is
/// entitled to rely on.
/// </summary>
public class ProviderContextTests
{
    private static (ProviderContext Context, Channel<EngineSignal> Channel) Create(int capacity = 16)
    {
        var channel = Channel.CreateBounded<EngineSignal>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        var context = new ProviderContext(
            "test.provider",
            channel.Writer,
            NullProviderLogger.Instance,
            new FakeTimeProvider(),
            NullLogger.Instance);

        return (context, channel);
    }

    private static List<EngineSignal> Drain(Channel<EngineSignal> channel)
    {
        var signals = new List<EngineSignal>();
        while (channel.Reader.TryRead(out var signal))
        {
            signals.Add(signal);
        }

        return signals;
    }

    [Fact]
    public void Publishing_sources_queues_them_against_the_publishing_provider()
    {
        var (context, channel) = Create();

        context.PublishSources([new AudioSourceDescriptor("a", "A", null)]);

        var published = Assert.IsType<SourcesPublished>(Assert.Single(Drain(channel)));
        Assert.Equal("test.provider", published.ProviderId);
        Assert.Equal(["a"], published.Sources.Select(source => source.SourceId));
    }

    [Fact]
    public void A_descriptor_with_a_blank_display_name_is_dropped_and_the_rest_of_the_set_survives()
    {
        var (context, channel) = Create();

        context.PublishSources(
        [
            new AudioSourceDescriptor("good", "Good", null),
            new AudioSourceDescriptor("blank", "   ", null),
            new AudioSourceDescriptor("also-good", "Also good", null),
        ]);

        // The descriptor is rejected, not the call. One malformed entry must
        // not silence a provider's whole catalogue.
        var published = Assert.IsType<SourcesPublished>(Assert.Single(Drain(channel)));
        Assert.Equal(["good", "also-good"], published.Sources.Select(source => source.SourceId));
    }

    [Fact]
    public void A_descriptor_with_a_blank_source_identifier_is_dropped()
    {
        var (context, channel) = Create();

        context.PublishSources(
        [
            new AudioSourceDescriptor("   ", "Nameless", null),
            new AudioSourceDescriptor("good", "Good", null),
        ]);

        var published = Assert.IsType<SourcesPublished>(Assert.Single(Drain(channel)));
        Assert.Equal(["good"], published.Sources.Select(source => source.SourceId));
    }

    [Fact]
    public void A_null_descriptor_inside_the_list_is_dropped_rather_than_throwing_into_the_provider()
    {
        var (context, channel) = Create();

        context.PublishSources([new AudioSourceDescriptor("good", "Good", null), null!]);

        var published = Assert.IsType<SourcesPublished>(Assert.Single(Drain(channel)));
        Assert.Equal(["good"], published.Sources.Select(source => source.SourceId));
    }

    [Fact]
    public void A_source_identifier_repeated_within_one_call_keeps_the_first_entry()
    {
        var (context, channel) = Create();

        context.PublishSources(
        [
            new AudioSourceDescriptor("dup", "First", null),
            new AudioSourceDescriptor("DUP", "Second", null),
        ]);

        var published = Assert.IsType<SourcesPublished>(Assert.Single(Drain(channel)));
        var source = Assert.Single(published.Sources);
        Assert.Equal("First", source.DisplayName);
    }

    [Fact]
    public void Publishing_an_empty_set_is_a_real_report_rather_than_a_no_op()
    {
        var (context, channel) = Create();

        // "PublishSources replaces rather than appends" means an empty set is how a provider says
        // it no longer has anything, so it must reach the consumer.
        context.PublishSources([]);

        var published = Assert.IsType<SourcesPublished>(Assert.Single(Drain(channel)));
        Assert.Empty(published.Sources);
    }

    [Fact]
    public void A_null_list_is_a_caller_bug_rather_than_an_empty_report()
    {
        var (context, _) = Create();

        Assert.Throws<ArgumentNullException>(() => context.PublishSources(null!));
    }

    [Fact]
    public void Reporting_presence_queues_the_source_and_the_value()
    {
        var (context, channel) = Create();

        context.ReportPresence("a", Presence.Present);

        var reported = Assert.IsType<PresenceReported>(Assert.Single(Drain(channel)));
        Assert.Equal("test.provider", reported.ProviderId);
        Assert.Equal("a", reported.SourceId);
        Assert.Equal(Presence.Present, reported.Presence);
    }

    [Fact]
    public void Reporting_presence_for_a_blank_source_is_dropped_rather_than_thrown()
    {
        var (context, channel) = Create();

        // A throw here would surface inside a vendor callback the host cannot see into, so this
        // is dropped and logged instead.
        context.ReportPresence("  ", Presence.Present);

        Assert.Empty(Drain(channel));
    }

    [Fact]
    public void Reporting_a_presence_value_no_member_declares_is_dropped()
    {
        var (context, channel) = Create();

        context.ReportPresence("a", (Presence)42);

        Assert.Empty(Drain(channel));
    }

    [Fact]
    public void Order_is_preserved_across_both_methods()
    {
        var (context, channel) = Create(capacity: 64);

        for (var i = 0; i < 20; i++)
        {
            context.PublishSources([new AudioSourceDescriptor($"s{i}", $"S{i}", null)]);
            context.ReportPresence($"s{i}", Presence.Present);
        }

        // Calls made by one provider take effect in the order it made them.
        var signals = Drain(channel);
        Assert.Equal(40, signals.Count);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal($"s{i}", Assert.IsType<SourcesPublished>(signals[i * 2]).Sources[0].SourceId);
            Assert.Equal($"s{i}", Assert.IsType<PresenceReported>(signals[(i * 2) + 1]).SourceId);
        }
    }

    [Fact]
    public async Task A_flooding_provider_waits_for_room_instead_of_losing_reports()
    {
        var (context, channel) = Create(capacity: 4);

        // Both methods may block, and neither drops. The write below cannot
        // complete until the reader takes something, which is exactly the backpressure the SDK
        // documents.
        var flooding = Task.Run(
            () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    context.ReportPresence($"s{i}", Presence.Present);
                }
            },
            TestContext.Current.CancellationToken);

        // Bounded so that a regression which drops reports fails here rather than hanging the
        // suite: with anything less than every report delivered, this read never completes.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        var received = 0;
        while (received < 200)
        {
            await channel.Reader.ReadAsync(timeout.Token);
            received++;
        }

        await flooding.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(200, received);
    }

    [Fact]
    public void A_report_made_after_the_pipeline_closed_is_logged_rather_than_thrown_at_the_provider()
    {
        var (context, channel) = Create();
        channel.Writer.Complete();

        // The SDK says both methods may be called from inside StopAsync, so a provider that logs
        // or reports while shutting down must not get an exception for it.
        context.ReportPresence("a", Presence.Absent);
        context.PublishSources([]);
    }

    [Fact]
    public void A_battery_report_reaches_the_queue()
    {
        var (context, channel) = Create();

        context.ReportBattery("src:one", 87, ChargeState.Discharging);

        var battery = Assert.IsType<BatteryReported>(Assert.Single(Drain(channel)));
        Assert.Equal("src:one", battery.SourceId);
        Assert.Equal(87, battery.Percent);
        Assert.Equal(ChargeState.Discharging, battery.Charge);
    }

    [Fact]
    public void A_battery_report_with_no_percentage_still_reaches_the_queue()
    {
        // How a provider declares "this source has a battery" before it knows the level. It is what
        // keeps a switched-off device on the battery page instead of removing it.
        var (context, channel) = Create();

        context.ReportBattery("src:one", percent: null, ChargeState.Unknown);

        var battery = Assert.IsType<BatteryReported>(Assert.Single(Drain(channel)));
        Assert.Null(battery.Percent);
        Assert.Equal(ChargeState.Unknown, battery.Charge);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void A_percentage_outside_zero_to_one_hundred_is_dropped(int percent)
    {
        var (context, channel) = Create();

        context.ReportBattery("src:one", percent, ChargeState.Discharging);

        Assert.Empty(Drain(channel));
    }

    [Fact]
    public void A_battery_report_for_a_blank_source_is_dropped()
    {
        var (context, channel) = Create();

        context.ReportBattery("   ", 50, ChargeState.Discharging);

        Assert.Empty(Drain(channel));
    }

    [Fact]
    public void A_charge_state_this_build_does_not_define_is_dropped()
    {
        var (context, channel) = Create();

        context.ReportBattery("src:one", 50, (ChargeState)99);

        Assert.Empty(Drain(channel));
    }

    [Fact]
    public void Zero_percent_is_a_real_reading_and_is_kept()
    {
        // A flat battery is exactly the reading a user most wants to see, and 0 is falsy in enough
        // languages that it is worth a test.
        var (context, channel) = Create();

        context.ReportBattery("src:one", 0, ChargeState.Discharging);

        Assert.Equal(0, Assert.IsType<BatteryReported>(Assert.Single(Drain(channel))).Percent);
    }
}

/// <summary>An <see cref="IProviderLogger"/> that records nothing, for tests that do not read logs.</summary>
internal sealed class NullProviderLogger : IProviderLogger
{
    internal static readonly NullProviderLogger Instance = new();

    public bool IsEnabled(ProviderLogLevel level) => false;

    public void Log(ProviderLogLevel level, string message, Exception? exception = null)
    {
    }
}
