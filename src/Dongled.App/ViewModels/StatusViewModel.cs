using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dongled.Abstractions;
using Dongled.App.Presentation;
using Dongled.App.Services;
using Dongled.Core.Audio;
using Dongled.Core.Configuration;
using Dongled.Core.Engine;
using Dongled.Core.Logging;
using Dongled.Core.Updates;

namespace Dongled.App.ViewModels;

/// <summary>One row in the detected-sources list.</summary>
/// <remarks>
/// A record so that value equality decides whether a poll changed anything. Comparing composed key
/// strings would need a separator, and any separator can appear inside a vendor's display name.
/// </remarks>
/// <param name="Name">The source's display name. Never an identifier; see <see cref="DisplayNames"/>.</param>
/// <param name="Detail">The provider's optional qualifier, or empty.</param>
/// <param name="State">A phrase describing presence.</param>
/// <param name="Glyph">A filled, hollow or dotted circle matching the state.</param>
/// <param name="Battery">The battery figure, or empty if this source does not report one.</param>
internal sealed record SourceRow(string Name, string Detail, string State, string Glyph, string Battery)
{
    /// <summary>Whether there is a qualifier worth showing beside the name.</summary>
    public bool HasDetail => Detail.Length > 0;

    /// <summary>Whether this source reports a battery at all.</summary>
    public bool HasBattery => Battery.Length > 0;
}

/// <summary>One line of recent activity.</summary>
/// <param name="Time">When it happened, as the user's short time.</param>
/// <param name="Text">The sentence, with names substituted for identifiers.</param>
internal sealed record ActivityRow(string Time, string Text);

/// <summary>
/// The landing page: what is default now, what sources exist, and what the app has done recently.
/// </summary>
/// <remarks>
/// <para>
/// Read-only by design: nothing here changes any state. It exists so the user can see what the app
/// thinks is going on.
/// </para>
/// <para>
/// Presence is polled rather than pushed. The engine deliberately exposes no "something changed"
/// event: it is a single-consumer loop whose whole point is that nothing outside it observes its
/// state as it mutates, and an event raised from inside that consumer would hand UI code a callback
/// on the thread owning all the policy. Two seconds is imperceptible for a headset being switched on
/// and costs one dictionary snapshot.
/// </para>
/// </remarks>
internal sealed partial class StatusViewModel : ObservableObject, IDisposable
{
    /// <summary>How many activity lines are shown. The Logs page is where the rest lives.</summary>
    private const int MaximumActivityRows = 50;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IAudioEndpointService _endpoints;
    private readonly ISwitchingEngine _engine;
    private readonly IConfigStore _configStore;
    private readonly LogRingBuffer _log;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUiTimer _timer;
    private readonly IUpdateService _updates;
    private readonly UpdateFlow _updateFlow;

    private bool _disposed;
    private double? _downloadProgress;

    /// <param name="endpoints">Windows audio, for the current defaults.</param>
    /// <param name="engine">The engine, for the source catalogue and its presence.</param>
    /// <param name="configStore">Configuration, for last-known names of anything absent.</param>
    /// <param name="log">The in-memory log, which recent activity is derived from.</param>
    /// <param name="dispatcher">The UI thread's queue.</param>
    /// <param name="updates">What the last update check found, for the banner.</param>
    /// <param name="updateFlow">What the banner's Update button runs.</param>
    public StatusViewModel(
        IAudioEndpointService endpoints,
        ISwitchingEngine engine,
        IConfigStore configStore,
        LogRingBuffer log,
        IUiDispatcher dispatcher,
        IUpdateService updates,
        UpdateFlow updateFlow)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(updateFlow);

        _endpoints = endpoints;
        _engine = engine;
        _configStore = configStore;
        _log = log;
        _dispatcher = dispatcher;
        _updates = updates;
        _updateFlow = updateFlow;

        _updates.Changed += OnUpdatesChanged;
        RefreshUpdate();

        // Seeded from history so opening the page shows what already happened rather than filling up
        // only from the moment it was opened.
        foreach (var entry in _log.Snapshot().Where(ActivityComposer.IsActivity))
        {
            AddActivity(entry);
        }

        _log.EntryAppended += OnEntryAppended;
        _endpoints.DefaultChanged += OnDefaultChanged;

        _timer = _dispatcher.CreateTimer(PollInterval, isRepeating: true, Refresh);
        _timer.Start();

        Refresh();
    }

    /// <summary>Sources any enabled plugin publishes, with a live presence indicator.</summary>
    public ObservableCollection<SourceRow> Sources { get; } = [];

    /// <summary>Recent activity, newest first.</summary>
    public ObservableCollection<ActivityRow> Activity { get; } = [];

    /// <summary>The endpoint holding the Media role.</summary>
    [ObservableProperty]
    public partial string MediaDefault { get; private set; } = "…";

    /// <summary>The endpoint holding the Calls role.</summary>
    [ObservableProperty]
    public partial string CallsDefault { get; private set; } = "…";

    /// <summary>
    /// Set when Windows audio could not be queried, so the page says so rather than showing "None"
    /// and letting the user conclude their speakers have vanished.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string Problem { get; private set; } = string.Empty;

    /// <summary>Whether <see cref="Problem"/> has anything to say.</summary>
    public bool HasProblem => Problem.Length > 0;

    /// <summary>Whether the sources list is empty, which the page explains rather than leaving blank.</summary>
    public bool HasNoSources => Sources.Count == 0;

    /// <summary>
    /// Whether the new-release banner shows: an official build, the setting on, a newer release
    /// known, and the user has not closed the banner for that release.
    /// </summary>
    [ObservableProperty]
    public partial bool IsUpdateBannerOpen { get; private set; }

    /// <summary>The banner's title.</summary>
    [ObservableProperty]
    public partial string UpdateBannerTitle { get; private set; } = string.Empty;

    /// <summary>The banner's line under the title: what the Update button does, or how far the download is.</summary>
    [ObservableProperty]
    public partial string UpdateBannerMessage { get; private set; } = string.Empty;

    /// <summary>The offered release's page, which holds its notes.</summary>
    [ObservableProperty]
    public partial Uri? ReleaseNotesUrl { get; private set; }

    /// <summary>Whether the Update button can be pressed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    public partial bool CanUpdate { get; private set; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _timer.Dispose();
        _log.EntryAppended -= OnEntryAppended;
        _endpoints.DefaultChanged -= OnDefaultChanged;
        _updates.Changed -= OnUpdatesChanged;
    }

    /// <summary>Close the banner for the release it offers. A newer release brings it back.</summary>
    [RelayCommand]
    private void DismissUpdate()
    {
        _updates.Dismiss();
        RefreshUpdate();
    }

    /// <summary>Download, verify and install the offered release, after asking.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateAsync()
    {
        _downloadProgress = null;

        await _updateFlow.RunAsync(new UiProgress<double>(_dispatcher, fraction =>
        {
            _downloadProgress = fraction;
            RefreshUpdate();
        }));

        _downloadProgress = null;
        RefreshUpdate();
    }

    private void RefreshUpdate()
    {
        if (_disposed)
        {
            return;
        }

        var snapshot = _updates.Current;

        IsUpdateBannerOpen = _updates.IsSupported
            && snapshot.AvailableVersion is not null
            && (snapshot.IsInstalling || (!snapshot.IsDismissed && _configStore.Load().App.CheckForUpdates));

        UpdateBannerTitle = snapshot.AvailableVersion is { } version ? UpdateText.Available(version) : string.Empty;
        UpdateBannerMessage = snapshot.IsInstalling
            ? UpdateText.Status(snapshot, checkOnOpen: true, _downloadProgress)
            : "Update downloads it from GitHub, checks it, and restarts Dongled.";
        ReleaseNotesUrl = snapshot.ReleaseNotesUrl;
        CanUpdate = IsUpdateBannerOpen && !snapshot.IsInstalling;
    }

    private void OnUpdatesChanged(object? sender, EventArgs e) =>
        _dispatcher.TryEnqueue(RefreshUpdate);

    private static string GlyphFor(Presence presence) => presence switch
    {
        Presence.Present => "●",
        Presence.Absent => "○",
        _ => "◌",
    };

    private void Refresh()
    {
        IReadOnlyList<AudioEndpoint> endpoints;
        DefaultEndpoints defaults;

        try
        {
            endpoints = _endpoints.Enumerate();
            defaults = _endpoints.GetDefaults();
            Problem = string.Empty;
        }
        catch (Exception ex)
        {
            // Documented as broad on the seam itself: reading a transient failure as "no endpoints"
            // would tell the user every device is gone.
            endpoints = [];
            defaults = default;
            Problem = $"Windows audio could not be queried: {ex.Message}";
        }

        MediaDefault = DisplayNames.ForDefault(endpoints, defaults.MultimediaId);
        CallsDefault = DisplayNames.ForDefault(endpoints, defaults.CommunicationsId);

        RefreshSources();
    }

    private void RefreshSources()
    {
        var rows = _engine.SourceStates()
            .Select(static state => new SourceRow(
                state.Descriptor.DisplayName,
                state.Descriptor.Detail ?? string.Empty,
                BatteryOrdering.StateFor(state.Presence),
                GlyphFor(state.Presence),
                state.Battery is null ? string.Empty : BatteryOrdering.Format(state.Battery)))
            .ToList();

        // Replaced only when something actually differs. Refilling an ObservableCollection every two
        // seconds would reset the list's scroll position while the user is reading it.
        if (rows.SequenceEqual(Sources))
        {
            return;
        }

        Sources.Clear();
        foreach (var row in rows)
        {
            Sources.Add(row);
        }

        OnPropertyChanged(nameof(HasNoSources));
    }

    private void AddActivity(LogEntry entry)
    {
        IReadOnlyList<AudioEndpoint> endpoints;
        try
        {
            endpoints = _endpoints.Enumerate();
        }
        catch (Exception)
        {
            endpoints = [];
        }

        var text = ActivityComposer.Compose(entry, endpoints, _engine.SourceStates(), _configStore.Load());
        if (text is null)
        {
            return;
        }

        Activity.Insert(0, new ActivityRow(
            entry.Timestamp.ToString("t", CultureInfo.CurrentCulture),
            text));

        while (Activity.Count > MaximumActivityRows)
        {
            Activity.RemoveAt(Activity.Count - 1);
        }
    }


    private void OnDefaultChanged(object? sender, DefaultChangedEventArgs e) =>
        _dispatcher.TryEnqueue(Refresh);

    // Raised on whichever thread logged, which is a provider thread or a timer continuation.
    private void OnEntryAppended(object? sender, LogEntryEventArgs e)
    {
        if (!ActivityComposer.IsActivity(e.Entry))
        {
            return;
        }

        _dispatcher.TryEnqueue(() => AddActivity(e.Entry));
    }
}
