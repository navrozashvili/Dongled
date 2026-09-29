using Dongled.Abstractions;

namespace Dongled.Plugins.Tests;

/// <summary>
/// An <see cref="IProviderLogger"/> that keeps what it was told, so a test can assert that a provider
/// explained itself rather than failing silently.
/// </summary>
/// <remarks>
/// The enabled level is configurable because a provider is expected to guard expensive messages with
/// <see cref="IProviderLogger.IsEnabled"/>, and a logger enabled for everything cannot catch a provider
/// that logs without checking. The comparison relies on <see cref="ProviderLogLevel"/> ascending by
/// severity with <see cref="ProviderLogLevel.None"/> last, which it does.
/// </remarks>
internal sealed class RecordingProviderLogger(ProviderLogLevel enabled = ProviderLogLevel.Trace)
    : IProviderLogger
{
    private readonly List<(ProviderLogLevel Level, string Message, Exception? Exception)> _entries = [];
    private readonly object _gate = new();

    /// <summary>Everything recorded, oldest first.</summary>
    public IReadOnlyList<(ProviderLogLevel Level, string Message, Exception? Exception)> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <inheritdoc />
    public bool IsEnabled(ProviderLogLevel level) => level != ProviderLogLevel.None && level >= enabled;

    /// <inheritdoc />
    public void Log(ProviderLogLevel level, string message, Exception? exception = null)
    {
        lock (_gate)
        {
            _entries.Add((level, message, exception));
        }
    }

    /// <summary>True if any recorded message contains <paramref name="fragment"/>.</summary>
    public bool Mentions(string fragment) =>
        Entries.Any(e => e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
