using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Logging;

/// <summary>
/// Writes log lines to one file per day under <see cref="Configuration.StoragePaths.LogDirectory"/>.
/// </summary>
/// <remarks>
/// <para>
/// The file name is <c>Dongled-yyyyMMdd.log</c> and the folder is the <c>logs</c>
/// subdirectory. Retention only ever deletes files matching that pattern in that folder, so it
/// cannot touch anything else the user keeps nearby.
/// </para>
/// <para>
/// Logging must never be the reason the app fails. Every write is guarded: a locked file, a full
/// disk, or a folder that cannot be created costs the file log and nothing else, because the ring
/// buffer is a separate provider and the UI reads that one.
/// </para>
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>
    /// How many of this app's own daily files are kept. Older ones are deleted when the provider
    /// starts.
    /// </summary>
    public const int RetainedFiles = 14;

    private const string FilePrefix = "Dongled-";
    private const string FileSuffix = ".log";

    private readonly Lock _gate = new();
    private readonly string _directory;
    private readonly TimeProvider _time;

    private StreamWriter? _writer;
    private string? _openFor;
    private bool _disabled;
    private bool _disposed;

    /// <param name="directory">The log folder, normally <see cref="Configuration.StoragePaths.LogDirectory"/>.</param>
    /// <param name="time">Clock for timestamps and for deciding which day's file to write.</param>
    public FileLoggerProvider(string directory, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(time);

        _directory = directory;
        _time = time;

        PruneOldFiles();
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        new FileLogger(this, categoryName ?? string.Empty);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var timestamp = _time.GetLocalNow();

        lock (_gate)
        {
            if (_disposed || _disabled)
            {
                return;
            }

            try
            {
                var writer = WriterFor(timestamp);

                writer.Write(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff K", CultureInfo.InvariantCulture));
                writer.Write("  ");
                writer.Write(Label(level));
                writer.Write("  ");
                writer.Write(category);
                writer.Write("  ");
                writer.WriteLine(message);

                if (exception is not null)
                {
                    writer.WriteLine(exception.ToString());
                }

                writer.Flush();
            }
            catch (Exception)
            {
                // Switched off rather than retried per line: whatever stopped the write is almost
                // certainly still true on the next one, and a failing write per log call would turn
                // a full disk into a hang. The ring buffer keeps working, so the Logs page still
                // shows this session.
                _disabled = true;
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    private static string Label(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT ",
        _ => "NONE ",
    };

    private StreamWriter WriterFor(DateTimeOffset timestamp)
    {
        var day = timestamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        if (_writer is not null && string.Equals(_openFor, day, StringComparison.Ordinal))
        {
            return _writer;
        }

        _writer?.Dispose();

        Directory.CreateDirectory(_directory);

        var path = Path.Combine(_directory, FilePrefix + day + FileSuffix);

        // Shared for reading so the Logs page's "Open folder" and any editor can read the file
        // while the app still has it open.
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _openFor = day;

        return _writer;
    }

    private void PruneOldFiles()
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            // Only this app's own pattern, in this app's own folder.
            var stale = new DirectoryInfo(_directory)
                .GetFiles(FilePrefix + "*" + FileSuffix)
                .OrderByDescending(file => file.Name, StringComparer.Ordinal)
                .Skip(RetainedFiles);

            foreach (var file in stale)
            {
                file.Delete();
            }
        }
        catch (Exception)
        {
            // Housekeeping. Not being able to tidy up is not a reason to fail to start.
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        internal FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

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

            _provider.Write(logLevel, _category, formatter(state, exception), exception);
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
            // Scopes are not written, so there is nothing to unwind.
        }
    }
}
