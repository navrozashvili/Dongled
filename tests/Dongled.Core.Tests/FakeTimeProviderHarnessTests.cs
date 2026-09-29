using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Dongled.Core.Tests;

/// <summary>
/// Proves the timing test strategy: a five-second stabilization delay must be assertable in
/// microseconds, not by sleeping.
/// </summary>
public class FakeTimeProviderHarnessTests
{
    [Fact]
    public async Task Advancing_fake_time_completes_a_five_second_delay_immediately()
    {
        var time = new FakeTimeProvider();
        var stopwatch = Stopwatch.StartNew();

        var delay = Task.Delay(TimeSpan.FromSeconds(5), time, TestContext.Current.CancellationToken);

        Assert.False(delay.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(5));
        await delay;

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"fake time did not short-circuit the delay; wall clock elapsed {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task A_delay_does_not_complete_before_its_due_time()
    {
        var time = new FakeTimeProvider();

        var delay = Task.Delay(TimeSpan.FromSeconds(5), time, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(4));

        Assert.False(delay.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1));
        await delay;

        Assert.True(delay.IsCompletedSuccessfully);
    }
}
