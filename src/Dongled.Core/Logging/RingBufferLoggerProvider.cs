using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Logging;

/// <summary>
/// Feeds every log line into a <see cref="LogRingBuffer"/> so the UI can show it without reading
/// a file back off disk.
/// </summary>
/// <remarks>
/// Holds no resources of its own: the buffer outlives it and is owned by whoever registered it, so
/// there is nothing here to dispose.
/// </remarks>
public sealed class RingBufferLoggerProvider : ILoggerProvider
{
    private readonly LogRingBuffer _buffer;
    private readonly TimeProvider _time;

    /// <param name="buffer">Where lines are kept.</param>
    /// <param name="time">Clock for each line's timestamp.</param>
    public RingBufferLoggerProvider(LogRingBuffer buffer, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(time);

        _buffer = buffer;
        _time = time;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        new RingBufferLogger(_buffer, _time, categoryName ?? string.Empty);

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing owned. Declared because ILoggerProvider requires it.
    }

    private sealed class RingBufferLogger : ILogger
    {
        private readonly LogRingBuffer _buffer;
        private readonly TimeProvider _time;
        private readonly string _category;

        internal RingBufferLogger(LogRingBuffer buffer, TimeProvider time, string category)
        {
            _buffer = buffer;
            _time = time;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        // Filtering is the logger factory's job: the levels a user picks in the UI are applied to
        // what is *displayed*, not to what is recorded, so switching the Logs page to Debug shows
        // history rather than only what happens next.
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (!IsEnabled(logLevel))
            {
                return;
            }

            var structured = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];

            _buffer.Append(new LogEntry(
                _time.GetLocalNow(),
                logLevel,
                _category,
                formatter(state, exception),
                exception?.ToString(),
                structured));
        }
    }

    private sealed class NullScope : IDisposable
    {
        internal static readonly NullScope Instance = new();

        private NullScope()
        {
        }

        public void Dispose()
        {
            // Scopes are not recorded, so there is nothing to unwind.
        }
    }
}
