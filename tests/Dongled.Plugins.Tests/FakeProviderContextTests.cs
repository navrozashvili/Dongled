using Dongled.Abstractions;
using Xunit;

namespace Dongled.Plugins.Tests;

public sealed class FakeProviderContextTests
{
    [Fact]
    public void A_published_list_is_snapshotted_so_a_provider_that_reuses_one_cannot_hide()
    {
        var context = new FakeProviderContext();
        var reused = new List<AudioSourceDescriptor>
        {
            new("a", "A", null),
        };

        context.PublishSources(reused);
        reused.Clear();
        reused.Add(new AudioSourceDescriptor("b", "B", null));
        context.PublishSources(reused);

        Assert.Equal(2, context.Publications.Count);
        Assert.Equal("a", Assert.Single(context.Publications[0]).SourceId);
        Assert.Equal("b", Assert.Single(context.Publications[1]).SourceId);
    }

    [Fact]
    public void PresenceOf_reports_the_most_recent_answer_and_the_history_keeps_the_order()
    {
        var context = new FakeProviderContext();

        context.ReportPresence("a", Presence.Absent);
        context.ReportPresence("b", Presence.Present);
        context.ReportPresence("a", Presence.Present);

        Assert.Equal(Presence.Present, context.PresenceOf("a"));
        Assert.Equal(Presence.Present, context.PresenceOf("b"));
        Assert.Null(context.PresenceOf("c"));
        Assert.Equal([Presence.Absent, Presence.Present], context.PresenceHistory("a"));
    }

    [Fact]
    public void The_recording_logger_is_disabled_below_its_level_and_never_enabled_for_None()
    {
        var context = new FakeProviderContext(ProviderLogLevel.Warning);

        Assert.False(context.Logger.IsEnabled(ProviderLogLevel.Information));
        Assert.True(context.Logger.IsEnabled(ProviderLogLevel.Warning));
        Assert.False(context.Logger.IsEnabled(ProviderLogLevel.None));

        context.Logger.Log(ProviderLogLevel.Warning, "the dongle went away");

        Assert.True(context.Recorded.Mentions("dongle"));
    }
}
