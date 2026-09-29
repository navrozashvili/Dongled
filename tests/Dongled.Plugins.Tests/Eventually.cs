using Xunit;

namespace Dongled.Plugins.Tests;

/// <summary>
/// Waits for something a provider's own thread will do. Both ported providers report from a thread of
/// their own, so an assertion made immediately after <c>StartAsync</c> races the provider.
/// </summary>
/// <remarks>
/// Fails with the caller's description rather than with a bare timeout, so a broken provider produces a
/// readable failure instead of "expected true, got false".
/// </remarks>
internal static class Eventually
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    public static async Task UntilAsync(Func<bool> condition, string description)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var deadline = DateTimeOffset.UtcNow + Limit;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            // xUnit1051 is an error here, so the test's own token goes to every call that takes one.
            await Task.Delay(PollInterval, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        Assert.Fail($"Timed out after {Limit.TotalSeconds:0.#}s waiting for: {description}");
    }
}
