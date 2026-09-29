using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dongled.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Policy;

/// <summary>
/// Holds a source's disconnect for the stabilization delay, so a device that flickers does not
/// cause a switch.
/// </summary>
/// <remarks>
/// <para>
/// Every method is called from the switching engine's single consumer, and the bookkeeping is
/// therefore unsynchronised on purpose. The timer callback is the one thing that runs elsewhere,
/// and it touches nothing but the queue: it puts a <see cref="StabilizationElapsed"/> on it and
/// lets the consumer decide, so the timer dictionary is never touched from two threads.
/// </para>
/// <para>
/// Timers run on <see cref="TimeProvider"/> so a five second delay is assertable in
/// microseconds rather than by sleeping.
/// </para>
/// </remarks>
internal sealed class StabilizationScheduler : IDisposable
{
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ChannelWriter<EngineSignal> _writer;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private long _nextEpoch;
    private bool _disposed;

    internal StabilizationScheduler(ChannelWriter<EngineSignal> writer, TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _writer = writer;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Start, or restart, this source's delay. Any delay already running for it is abandoned.
    /// </summary>
    internal void Schedule(string sourceId, TimeSpan delay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        Cancel(sourceId);

        if (_disposed)
        {
            return;
        }

        var pending = new Pending(++_nextEpoch);

        // A zero stabilization delay is allowed, and the configuration store clamps to a floor of
        // zero rather than forbidding it. That makes the timer fire immediately, which raises the
        // question of whether the bookkeeping below can be recorded too late for the signal that
        // is already on its way. It cannot: the callback touches nothing but the queue, and the
        // only two methods that read this dictionary are called from the engine's single
        // consumer, which is the same thread that is inside this method. There is no interleaving
        // to guard against.
        var timer = _time.CreateTimer(
            static state =>
            {
                var (scheduler, source, epoch) = ((StabilizationScheduler, string, long))state!;
                scheduler.OnElapsed(source, epoch);
            },
            (this, sourceId, pending.Epoch),
            delay,
            Timeout.InfiniteTimeSpan);

        pending.Timer = timer;
        _pending[sourceId] = pending;
    }

    /// <summary>Abandon this source's delay. Reports whether there was one.</summary>
    internal bool Cancel(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        if (!_pending.Remove(sourceId, out var pending))
        {
            return false;
        }

        pending.Timer?.Dispose();
        return true;
    }

    /// <summary>Whether this source is waiting out a delay.</summary>
    internal bool HasPending(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        return _pending.ContainsKey(sourceId);
    }

    /// <summary>
    /// Accept an elapsed signal and clear the scheduling it belongs to. Reports false for a
    /// signal from a scheduling that has since been superseded or cancelled, which is how a
    /// reconnect inside the window makes an already-queued signal harmless.
    /// </summary>
    internal bool TryComplete(string sourceId, long epoch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        if (!_pending.TryGetValue(sourceId, out var pending) || pending.Epoch != epoch)
        {
            return false;
        }

        _pending.Remove(sourceId);
        pending.Timer?.Dispose();
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var pending in _pending.Values)
        {
            pending.Timer?.Dispose();
        }

        _pending.Clear();
    }

    private void OnElapsed(string sourceId, long epoch)
    {
        var signal = new StabilizationElapsed(_time.GetUtcNow(), sourceId, epoch);

        // TryWrite rather than a wait, because this can run on the consumer's own thread when the
        // delay is zero, and waiting there would block the only thread that drains the queue.
        if (_writer.TryWrite(signal))
        {
            return;
        }

        // The queue is momentarily full, so hand the write to the thread pool. A stabilization
        // signal has no ordering relationship with a provider's reports, and the consumer
        // re-checks presence before acting on it, so arriving late is harmless.
        _ = Task.Run(async () =>
        {
            try
            {
                await _writer.WriteAsync(signal, CancellationToken.None);
            }
            catch (ChannelClosedException)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "The stabilization delay for {SourceId} elapsed after shutdown; no return was applied.",
                        sourceId);
                }
            }
        });
    }

    /// <summary>One source's running delay.</summary>
    /// <remarks>
    /// A class rather than a record struct because the timer is assigned after construction: the
    /// entry has to be findable before the timer that references it exists.
    /// </remarks>
    private sealed class Pending
    {
        internal Pending(long epoch) => Epoch = epoch;

        internal long Epoch { get; }

        internal ITimer? Timer { get; set; }
    }
}
