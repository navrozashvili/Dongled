using Dongled.Abstractions;
using Dongled.Plugin.HyperXHid;
using Xunit;

namespace Dongled.Plugins.Tests.HyperXHid;

/// <summary>
/// The one test that talks to a real dongle, and so the only coverage
/// <see cref="HidSharpHeadsetTransport"/> has. Skips rather than fails without one, because CI and
/// most developers have no HyperX hardware.
/// </summary>
public sealed class HyperXHidHardwareTests
{
    [Fact]
    public async Task The_real_dongle_opens_and_the_provider_reaches_a_verdict()
    {
        var provider = new HyperXHidProvider();
        var context = new FakeProviderContext();

        await provider.StartAsync(context, TestContext.Current.CancellationToken);

        try
        {
            // A verdict either way is the pass condition: Present if the headset is on, Absent if it is
            // off or the dongle is unplugged. What is under test is that the HidSharp adapter produces
            // an answer at all, which no other test can show.
            await Eventually.UntilAsync(
                () => context.PresenceOf(HyperXHidProvider.SourceId) is not null,
                "the real transport to reach a verdict");

            // "Dongle open" is logged only after a session was opened, which is how this tells "no
            // dongle on this machine" from "dongle present, headset switched off" without a second API.
            if (!context.Recorded.Mentions("Dongle open"))
            {
                Assert.Skip("No HyperX Cloud III S dongle on this machine, so the real transport cannot be exercised.");
            }

            Assert.NotEqual(Presence.Unknown, context.PresenceOf(HyperXHidProvider.SourceId));
        }
        finally
        {
            await provider.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
