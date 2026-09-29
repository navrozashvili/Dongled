using System.Collections.Generic;
using System.Threading;

namespace Dongled.Core.Logging;

/// <summary>A line was appended to the ring buffer.</summary>
public sealed class LogEntryEventArgs : EventArgs
{
    /// <param name="entry">The line that was appended.</param>
    public LogEntryEventArgs(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Entry = entry;
    }

    /// <summary>The line that was appended.</summary>
    public LogEntry Entry { get; }
}

/// <summary>
/// The last <see cref="Capacity"/> log lines, oldest evicted first. This is what the Logs page
/// tails and what the Status page reads its recent activity from.
/// </summary>
/// <remarks>
/// <para>
/// Bounded on purpose. A provider that logs in a loop would otherwise grow this without limit for
/// as long as the app runs, and an in-memory log that can exhaust memory is worse than no
/// in-memory log.
/// </para>
/// <para>
/// Every member is safe to call from any thread. Log lines arrive on provider threads, on timer
/// continuations, and on the UI thread; <see cref="Snapshot"/> is taken under the same lock that
/// appends, so a reader never sees a half-written buffer.
/// </para>
/// <para>
/// <see cref="EntryAppended"/> is raised <em>outside</em> the lock, on whichever thread logged.
/// A handler must therefore marshal to the UI thread itself, and must not block: it is running on
/// a thread that is in the middle of doing something else.
/// </para>
/// </remarks>
public sealed class LogRingBuffer
{
    /// <summary>
    /// How many lines are kept. Enough that the Logs page opens on a useful amount of history,
    /// small enough to be irrelevant to the process's memory.
    /// </summary>
    public const int Capacity = 2000;

    private readonly Lock _gate = new();
    private readonly LogEntry?[] _entries = new LogEntry?[Capacity];

    private int _next;
    private int _count;

    /// <summary>
    /// Raised after a line is appended, on the thread that logged it. See the remarks on this type
    /// before handling it.
    /// </summary>
    public event EventHandler<LogEntryEventArgs>? EntryAppended;

    /// <summary>How many lines are currently held, never more than <see cref="Capacity"/>.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Record one line, evicting the oldest if the buffer is full.</summary>
    /// <param name="entry">The line to record.</param>
    public void Append(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            _entries[_next] = entry;
            _next = (_next + 1) % Capacity;

            if (_count < Capacity)
            {
                _count++;
            }
        }

        // Outside the lock: a handler that logged, or that took a lock of its own, would otherwise
        // deadlock against the next thread trying to append.
        var handler = EntryAppended;
        if (handler is not null)
        {
            handler(this, new LogEntryEventArgs(entry));
        }
    }

    /// <summary>Every line currently held, oldest first.</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            var snapshot = new List<LogEntry>(_count);

            // The oldest line is the one after the newest once the buffer has wrapped, and index 0
            // until it has.
            var start = _count == Capacity ? _next : 0;

            for (var offset = 0; offset < _count; offset++)
            {
                var entry = _entries[(start + offset) % Capacity];
                if (entry is not null)
                {
                    snapshot.Add(entry);
                }
            }

            return snapshot;
        }
    }

    /// <summary>Forget everything held, which the Logs page's clear action does.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_entries);
            _next = 0;
            _count = 0;
        }
    }
}
