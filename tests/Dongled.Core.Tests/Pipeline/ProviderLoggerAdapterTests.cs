using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dongled.Abstractions;
using Dongled.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Dongled.Core.Tests.Pipeline;

/// <summary>
/// Each of these corresponds to a sentence on <see cref="IProviderLogger"/>,
/// which is a versioned boundary: a plugin author reads that documentation and writes code
/// against it.
/// </summary>
public class ProviderLoggerAdapterTests
{
    private static (ProviderLoggerAdapter Adapter, RecordingLogger Sink, Box<LogLevel> Minimum) Create(
        LogLevel minimum = LogLevel.Warning)
    {
        var sink = new RecordingLogger();
        var box = new Box<LogLevel> { Value = minimum };
        return (new ProviderLoggerAdapter(sink, () => box.Value), sink, box);
    }

    [Fact]
    public void The_default_minimum_is_warning_in_every_build_configuration()
    {
        var (adapter, _, _) = Create();

        Assert.False(adapter.IsEnabled(ProviderLogLevel.Information));
        Assert.True(adapter.IsEnabled(ProviderLogLevel.Warning));
        Assert.True(adapter.IsEnabled(ProviderLogLevel.Error));
    }

    [Fact]
    public void None_is_never_enabled_even_when_it_is_the_configured_minimum()
    {
        var (adapter, _, minimum) = Create(LogLevel.None);

        // None is a threshold meaning "record nothing", never a severity. A naive
        // level >= minimum comparison answers true here and is wrong.
        Assert.False(adapter.IsEnabled(ProviderLogLevel.None));
        Assert.False(adapter.IsEnabled(ProviderLogLevel.Critical));

        minimum.Value = LogLevel.Trace;
        Assert.False(adapter.IsEnabled(ProviderLogLevel.None));
        Assert.True(adapter.IsEnabled(ProviderLogLevel.Critical));
    }

    [Fact]
    public void A_configured_minimum_of_none_records_nothing_at_any_level()
    {
        var (adapter, sink, _) = Create(LogLevel.None);

        foreach (var level in Enum.GetValues<ProviderLogLevel>())
        {
            adapter.Log(level, "should not appear");
        }

        Assert.Empty(sink.Entries);
    }

    [Fact]
    public void Filtering_is_per_plugin_and_follows_the_configured_level_live()
    {
        var (adapter, sink, minimum) = Create();

        adapter.Log(ProviderLogLevel.Information, "below the threshold");
        Assert.Empty(sink.Entries);

        minimum.Value = LogLevel.Debug;
        adapter.Log(ProviderLogLevel.Information, "now above it");

        var entry = Assert.Single(sink.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("now above it", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_level_no_member_declares_is_refused_rather_than_mapped_to_something()
    {
        var (adapter, sink, _) = Create(LogLevel.Trace);

        Assert.False(adapter.IsEnabled((ProviderLogLevel)99));
        adapter.Log((ProviderLogLevel)99, "should not appear");

        Assert.Empty(sink.Entries);
    }

    [Fact]
    public void An_exception_is_carried_through()
    {
        var (adapter, sink, _) = Create();
        var failure = new InvalidOperationException("boom");

        adapter.Log(ProviderLogLevel.Error, "it broke", failure);

        Assert.Same(failure, Assert.Single(sink.Entries).Exception);
    }

    [Fact]
    public void A_null_message_is_recorded_rather_than_thrown()
    {
        var (adapter, sink, _) = Create();

        adapter.Log(ProviderLogLevel.Warning, null!);

        Assert.Single(sink.Entries);
    }

    [Fact]
    public async Task Both_members_are_safe_to_call_from_many_threads_at_once()
    {
        var (adapter, sink, _) = Create(LogLevel.Trace);

        // Not a courtesy: a HID or socket provider logs from a vendor callback on a thread the
        // host never created and cannot marshal off.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(
            () =>
            {
                for (var i = 0; i < 250; i++)
                {
                    if (adapter.IsEnabled(ProviderLogLevel.Information))
                    {
                        adapter.Log(ProviderLogLevel.Information, $"worker {worker} line {i}");
                    }
                }
            },
            TestContext.Current.CancellationToken)));

        Assert.Equal(2000, sink.Entries.Count);
    }
}

/// <summary>A mutable cell, so a test can change the configured level after construction.</summary>
internal sealed class Box<T>
{
    public T Value { get; set; } = default!;
}

/// <summary>An <see cref="ILogger"/> that keeps what it was told, and is safe to write from many threads.</summary>
internal sealed class RecordingLogger : ILogger
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    /// <summary>Everything recorded, in no particular order across threads.</summary>
    public IReadOnlyCollection<LogEntry> Entries => _entries;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));

    /// <summary>One recorded line.</summary>
    public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
