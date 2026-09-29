using System.Threading;
using System.Threading.Tasks;
using Dongled.Abstractions;
using Dongled.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Dongled.Core.Tests.Pipeline;

/// <summary>
/// The start deadline and the exactly-once stop contract, both documented on <see cref="IAudioSourceProvider"/>.
/// </summary>
public class ProviderRunnerTests
{
    private static ProviderRunner Create(FakeProvider provider, FakeTimeProvider time) =>
        new(provider, new NoOpProviderContext(), time, NullLogger.Instance);

    [Fact]
    public async Task A_provider_that_starts_cleanly_is_running()
    {
        var provider = new FakeProvider();
        var time = new FakeTimeProvider();
        using var runner = Create(provider, time);

        var started = await runner.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(started);
        Assert.Equal(ProviderRunState.Running, runner.State);
        Assert.Equal(1, provider.StartCalls);
        Assert.Equal(0, provider.StopCalls);
    }

    [Fact]
    public async Task Stopping_a_running_provider_stops_it_once()
    {
        var provider = new FakeProvider();
        var time = new FakeTimeProvider();
        using var runner = Create(provider, time);
        await runner.StartAsync(TestContext.Current.CancellationToken);

        await runner.StopAsync(TestContext.Current.CancellationToken);
        await runner.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, provider.StopCalls);
        Assert.Equal(ProviderRunState.Stopped, runner.State);
    }

    [Fact]
    public async Task A_provider_whose_start_threw_synchronously_still_gets_its_cleanup_call()
    {
        // The trigger is "StartAsync was called", not "StartAsync succeeded". The natural
        // implementation abandons a provider whose start threw, which leaks whatever it opened
        // before throwing.
        var provider = new FakeProvider { StartBehaviour = StartBehaviour.ThrowSynchronously };
        var time = new FakeTimeProvider();
        using var runner = Create(provider, time);

        var started = await runner.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(started);
        Assert.Equal(ProviderRunState.Failed, runner.State);
        Assert.Equal(1, provider.StopCalls);
    }

    [Fact]
    public async Task A_provider_whose_start_returned_a_faulted_task_still_gets_its_cleanup_call()
    {
        var provider = new FakeProvider { StartBehaviour = StartBehaviour.ReturnFaultedTask };
        var time = new FakeTimeProvider();
        using var runner = Create(provider, time);

        var started = await runner.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(started);
        Assert.Equal(ProviderRunState.Failed, runner.State);
        Assert.Equal(1, provider.StopCalls);
    }

    [Fact]
    public async Task A_provider_that_misses_the_start_deadline_fails_and_is_stopped_exactly_once()
    {
        var provider = new FakeProvider { StartBehaviour = StartBehaviour.NeverReturn };
        var time = new FakeTimeProvider();
        using var runner = Create(provider, time);

        var starting = runner.StartAsync(TestContext.Current.CancellationToken);
        Assert.False(starting.IsCompleted);

        time.Advance(ProviderRunner.StartDeadline);
        var started = await starting;

        Assert.False(started);
        Assert.Equal(ProviderRunState.Failed, runner.State);

        // The exactly-once half is what forces a state machine rather than a finally in two
        // places: without it the timeout path stops it and shutdown stops it again.
        await runner.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, provider.StopCalls);
    }

    [Fact]
    public async Task Shutting_down_during_start_stops_the_provider_once_without_calling_it_a_timeout()
    {
        var provider = new FakeProvider { StartBehaviour = StartBehaviour.NeverReturn };
        var time = new FakeTimeProvider();
        var logger = new RecordingLogger();
        using var runner = new ProviderRunner(provider, new NoOpProviderContext(), time, logger);
        using var shutdown = new CancellationTokenSource();

        var starting = runner.StartAsync(shutdown.Token);
        await shutdown.CancelAsync();
        var started = await starting.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.False(started);
        Assert.Equal(ProviderRunState.Stopped, runner.State);
        Assert.Equal(1, provider.StopCalls);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("shutting down", StringComparison.Ordinal));

        // Shutdown then stops every runner; the provider must not get a second cleanup call.
        await runner.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, provider.StopCalls);
    }

    [Fact]
    public async Task A_provider_stopped_while_its_start_was_pending_is_not_reported_as_running()
    {
        var provider = new FakeProvider { StartBehaviour = StartBehaviour.NeverReturn };
        var time = new FakeTimeProvider();
        using var runner = Create(provider, time);

        var starting = runner.StartAsync(TestContext.Current.CancellationToken);
        await runner.StopAsync(TestContext.Current.CancellationToken);
        provider.LetStartComplete();

        Assert.False(await starting.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal(ProviderRunState.Stopped, runner.State);
        Assert.Equal(1, provider.StopCalls);
    }

    [Fact]
    public async Task A_failed_provider_stays_failed_after_shutdown_stops_it()
    {
        var provider = new FakeProvider { StartBehaviour = StartBehaviour.ReturnFaultedTask };
        var time = new FakeTimeProvider();
        using var runner = Create(provider, time);

        await runner.StartAsync(TestContext.Current.CancellationToken);
        await runner.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderRunState.Failed, runner.State);
    }

    [Fact]
    public async Task A_provider_still_inside_the_deadline_is_not_failed()
    {
        var provider = new FakeProvider { StartBehaviour = StartBehaviour.NeverReturn };
        var time = new FakeTimeProvider();
        using var runner = Create(provider, time);

        var starting = runner.StartAsync(TestContext.Current.CancellationToken);
        time.Advance(ProviderRunner.StartDeadline - TimeSpan.FromMilliseconds(1));

        Assert.False(starting.IsCompleted);
        Assert.Equal(ProviderRunState.NotStarted, runner.State);

        provider.LetStartComplete();
        Assert.True(await starting);
        Assert.Equal(ProviderRunState.Running, runner.State);
    }

    [Fact]
    public async Task The_cancellation_token_a_provider_is_given_stays_valid_after_start_returns()
    {
        var provider = new FakeProvider();
        var time = new FakeTimeProvider();
        using var runner = Create(provider, time);
        await runner.StartAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(provider.ObservedToken);
        Assert.False(provider.ObservedToken!.Value.IsCancellationRequested);

        await runner.StopAsync(TestContext.Current.CancellationToken);

        // The watch loop has to be told to unwind before StopAsync is asked to release things.
        Assert.True(provider.TokenWasCancelledBeforeStop);
    }

    [Fact]
    public async Task A_provider_that_was_never_started_is_not_given_a_cleanup_call()
    {
        var provider = new FakeProvider();
        var time = new FakeTimeProvider();
        using var runner = Create(provider, time);

        await runner.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, provider.StopCalls);
        Assert.Equal(ProviderRunState.NotStarted, runner.State);
    }

    [Fact]
    public async Task Starting_twice_is_refused_so_a_failed_provider_is_not_retried()
    {
        var provider = new FakeProvider();
        var time = new FakeTimeProvider();
        using var runner = Create(provider, time);
        await runner.StartAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, provider.StartCalls);
    }

    [Fact]
    public async Task A_failing_cleanup_call_still_leaves_the_provider_stopped()
    {
        var provider = new FakeProvider { ThrowOnStop = true };
        var time = new FakeTimeProvider();
        using var runner = Create(provider, time);
        await runner.StartAsync(TestContext.Current.CancellationToken);

        await runner.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderRunState.Stopped, runner.State);
    }
}

/// <summary>An <see cref="IProviderContext"/> that accepts everything and remembers nothing.</summary>
internal sealed class NoOpProviderContext : IProviderContext
{
    public IProviderLogger Logger { get; } = NullProviderLogger.Instance;

    public void PublishSources(System.Collections.Generic.IReadOnlyList<AudioSourceDescriptor> sources)
    {
    }

    public void ReportPresence(string sourceId, Presence presence)
    {
    }

    public void ReportBattery(string sourceId, int? percent, ChargeState charge)
    {
    }
}

/// <summary>How the fake provider's start should behave.</summary>
internal enum StartBehaviour
{
    /// <summary>Return a completed task.</summary>
    CompleteImmediately,

    /// <summary>Throw before returning a task at all.</summary>
    ThrowSynchronously,

    /// <summary>Return a task that is already faulted.</summary>
    ReturnFaultedTask,

    /// <summary>Return a task that never completes until told to.</summary>
    NeverReturn,
}

/// <summary>A provider whose lifecycle a test drives.</summary>
internal sealed class FakeProvider : IAudioSourceProvider
{
    private readonly TaskCompletionSource _startGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ProviderMetadata Metadata { get; } = new("fake", "Fake provider", null, false);

    public StartBehaviour StartBehaviour { get; set; } = StartBehaviour.CompleteImmediately;

    public bool ThrowOnStop { get; set; }

    public int StartCalls { get; private set; }

    public int StopCalls { get; private set; }

    public CancellationToken? ObservedToken { get; private set; }

    public bool TokenWasCancelledBeforeStop { get; private set; }

    public void LetStartComplete() => _startGate.TrySetResult();

    public Task StartAsync(IProviderContext context, CancellationToken ct)
    {
        StartCalls++;
        ObservedToken = ct;

        return StartBehaviour switch
        {
            StartBehaviour.ThrowSynchronously => throw new InvalidOperationException("start refused"),
            StartBehaviour.ReturnFaultedTask => Task.FromException(new InvalidOperationException("start failed")),
            StartBehaviour.NeverReturn => _startGate.Task,
            _ => Task.CompletedTask,
        };
    }

    public Task StopAsync(CancellationToken ct)
    {
        StopCalls++;
        TokenWasCancelledBeforeStop = ObservedToken?.IsCancellationRequested ?? false;

        if (ThrowOnStop)
        {
            throw new InvalidOperationException("stop refused");
        }

        return Task.CompletedTask;
    }
}
