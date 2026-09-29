using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dongled.App.Services;
using Dongled.Core.Configuration;
using Dongled.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Dongled.App.ViewModels;

/// <summary>One line in the log view.</summary>
/// <param name="Time">When it was recorded.</param>
/// <param name="Level">The short severity label.</param>
/// <param name="Category">Which logger recorded it.</param>
/// <param name="Message">The rendered message, and the exception text if there was one.</param>
internal sealed record LogRow(string Time, string Level, string Category, string Message);

/// <summary>
/// The Logs page: a live tail with a level filter, a way into the folder, and a copy that is
/// safe to paste into a bug report.
/// </summary>
/// <remarks>
/// Reads the in-memory ring buffer rather than the file on disk. The file is the durable record and
/// survives a restart; the buffer is what can be shown as it happens, and it holds lines at a lower
/// severity than the file keeps, so lowering the filter reveals history instead of only affecting
/// what arrives next.
/// </remarks>
internal sealed partial class LogsViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// The severities offered, in the order the page lists them. Trace is absent because the ring
    /// buffer is filtered at Debug, so offering it would be a filter that can only ever show less.
    /// </summary>
    private static readonly LogLevel[] Levels =
    [
        LogLevel.Debug,
        LogLevel.Information,
        LogLevel.Warning,
        LogLevel.Error,
    ];

    private readonly LogRingBuffer _log;
    private readonly IUiDispatcher _dispatcher;

    private int _levelIndex = 1;
    private bool _disposed;

    /// <param name="log">The in-memory log.</param>
    /// <param name="dispatcher">The UI thread's queue.</param>
    public LogsViewModel(LogRingBuffer log, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _log = log;
        _dispatcher = dispatcher;


        Rebuild();

        _log.EntryAppended += OnEntryAppended;
    }

    /// <summary>The lines currently shown, newest first.</summary>
    public ObservableCollection<LogRow> Entries { get; } = [];

    /// <summary>Which severity to show and above, as an index into the offered levels.</summary>
    public int LevelIndex
    {
        get => _levelIndex;
        set
        {
            if (SetProperty(ref _levelIndex, value))
            {
                Rebuild();
            }
        }
    }

    /// <summary>Whether the filtered view is empty, which the page explains rather than leaving blank.</summary>
    public bool HasNoEntries => Entries.Count == 0;

    /// <summary>Set when an action failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string Problem { get; private set; } = string.Empty;

    /// <summary>Whether <see cref="Problem"/> has anything to say.</summary>
    public bool HasProblem => Problem.Length > 0;

    /// <summary>Set to confirm an action that produced no visible result, such as a copy.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string Notice { get; private set; } = string.Empty;

    /// <summary>Whether <see cref="Notice"/> has anything to say.</summary>
    public bool HasNotice => Notice.Length > 0;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _log.EntryAppended -= OnEntryAppended;
    }

    /// <summary>
    /// Remove anything that identifies the person running the app from text destined for a bug
    /// report.
    /// </summary>
    /// <param name="text">The text to sanitize.</param>
    /// <remarks>
    /// <para>
    /// The profile path is replaced first and the bare user name second, because the path contains
    /// the name: replacing the name first would turn <c>C:\Users\alice\…</c> into
    /// <c>C:\Users\%USERNAME%\…</c> and the longer, more precise replacement would then never match.
    /// </para>
    /// <para>
    /// Ordinal and case-insensitive, because Windows paths compare that way and a log line may hold
    /// either casing.
    /// </para>
    /// </remarks>
    public static string StripUserIdentity(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        var user = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(user))
        {
            text = text.Replace(user, "%USERNAME%", StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    private void Rebuild()
    {
        var minimum = MinimumLevel();

        var rows = _log.Snapshot()
            .Where(entry => entry.Level >= minimum)
            .Reverse()
            .Select(ToRow)
            .ToList();

        Entries.Clear();
        foreach (var row in rows)
        {
            Entries.Add(row);
        }

        OnPropertyChanged(nameof(HasNoEntries));
    }

    private static LogRow ToRow(LogEntry entry)
    {
        var message = entry.Exception is null
            ? entry.Message
            : entry.Message + Environment.NewLine + entry.Exception;

        return new LogRow(
            entry.Timestamp.ToString("HH:mm:ss", CultureInfo.CurrentCulture),
            entry.LevelLabel,
            entry.Category,
            message);
    }

    private LogLevel MinimumLevel() =>
        _levelIndex >= 0 && _levelIndex < Levels.Length ? Levels[_levelIndex] : LogLevel.Information;

    private void OnEntryAppended(object? sender, LogEntryEventArgs e)
    {
        if (e.Entry.Level < MinimumLevel())
        {
            return;
        }

        // Raised on whichever thread logged, which is never the UI thread.
        _dispatcher.TryEnqueue(() =>
        {
            Entries.Insert(0, ToRow(e.Entry));

            // The buffer bounds itself; this bounds what the list is holding, which is what costs
            // layout work.
            while (Entries.Count > LogRingBuffer.Capacity)
            {
                Entries.RemoveAt(Entries.Count - 1);
            }

            OnPropertyChanged(nameof(HasNoEntries));
        });
    }

    /// <summary>Opens the log folder in Explorer.</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(StoragePaths.LogDirectory);

            using var process = Process.Start(new ProcessStartInfo(StoragePaths.LogDirectory)
            {
                UseShellExecute = true,
            });

            Problem = string.Empty;
        }
        catch (Exception ex)
        {
            Problem = $"The log folder could not be opened: {ex.Message}";
        }
    }

    /// <summary>Copies what is shown, with the user's name removed from any path.</summary>
    [RelayCommand]
    private void CopyForBugReport()
    {
        try
        {
            var text = new StringBuilder();

            // Oldest first in the copy, whichever way the list is showing them: a log someone else
            // has to read should run forwards.
            foreach (var row in Entries.Reverse())
            {
                text.Append(row.Time)
                    .Append("  ")
                    .Append(row.Level)
                    .Append("  ")
                    .Append(row.Category)
                    .Append("  ")
                    .AppendLine(row.Message);
            }

            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(StripUserIdentity(text.ToString()));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);

            Problem = string.Empty;
            Notice = $"{Entries.Count} line(s) copied, with your user name removed from any path.";
        }
        catch (Exception ex)
        {
            Notice = string.Empty;
            Problem = $"The log could not be copied: {ex.Message}";
        }
    }

    /// <summary>Empties the in-memory log. The files on disk are untouched.</summary>
    [RelayCommand]
    private void Clear()
    {
        _log.Clear();
        Rebuild();
        Notice = "The in-memory log was cleared. The files on disk are unchanged.";
    }
}
