using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dongled.Abstractions;
using Dongled.App.Presentation;
using Dongled.App.Services;
using Dongled.Core.Configuration;
using Dongled.Core.Engine;
using Dongled.Core.Pipeline;

namespace Dongled.App.ViewModels;

/// <summary>One battery-capable source, as a row the user can move.</summary>
/// <remarks>
/// A mutable observable rather than a record, unlike <see cref="SourceRow"/>. This list is
/// reconciled in place on every poll so moving a row does not fight the timer for the selection,
/// and reconciling in place needs objects whose properties can change.
/// </remarks>
internal sealed partial class BatteryRow : ObservableObject
{
    public BatteryRow(string sourceId) => SourceId = sourceId;

    /// <summary>Identity. Never displayed; it is what the saved order is written in terms of.</summary>
    public string SourceId { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    public partial string Detail { get; set; } = string.Empty;

    public bool HasDetail => Detail.Length > 0;

    /// <summary>The battery figure, or an em dash when nothing is being reported.</summary>
    [ObservableProperty]
    public partial string Level { get; set; } = "—";

    /// <summary>Whether the device is connected, in the words the Status page uses.</summary>
    [ObservableProperty]
    public partial string State { get; set; } = string.Empty;

    /// <summary>Whether this is the row the notification-area icon is currently showing.</summary>
    [ObservableProperty]
    public partial bool IsTrayPick { get; set; }
}

/// <summary>
/// Every source a plugin reports a battery for, in the order the user chose, with the one the tray
/// speaks for marked.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the Status page because this one writes. Status is documented read-only by design
/// and a reorder control on it would quietly end that.
/// </para>
/// <para>
/// Polled on the same two-second timer as Status, for the same reason: the engine deliberately
/// raises no "something changed" event, because an event from inside its single consumer would hand
/// UI code a callback on the thread that owns every rule.
/// </para>
/// <para>Every reorder persists immediately, matching the Settings page.</para>
/// </remarks>
internal sealed partial class BatteryViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly ISwitchingEngine _engine;
    private readonly IConfigStore _configStore;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUiTimer _timer;
    private readonly Action _notifyTrayOrderChanged;

    private bool _disposed;

    /// <param name="engine">The engine, for battery readings.</param>
    /// <param name="configStore">Configuration, for the user's battery order.</param>
    /// <param name="dispatcher">The UI thread's queue.</param>
    /// <param name="notifyTrayOrderChanged">
    /// Called after a reorder is persisted, so the notification-area icon updates immediately rather
    /// than waiting for its own poll.
    /// </param>
    public BatteryViewModel(
        ISwitchingEngine engine,
        IConfigStore configStore,
        IUiDispatcher dispatcher,
        Action notifyTrayOrderChanged)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(notifyTrayOrderChanged);

        _engine = engine;
        _configStore = configStore;
        _dispatcher = dispatcher;
        _notifyTrayOrderChanged = notifyTrayOrderChanged;

        _timer = _dispatcher.CreateTimer(PollInterval, isRepeating: true, Refresh);
        _timer.Start();

        Refresh();
    }

    /// <summary>Battery-capable sources, most preferred first.</summary>
    public ObservableCollection<BatteryRow> Rows { get; } = [];

    /// <summary>Which row the move commands act on.</summary>
    [ObservableProperty]
    public partial BatteryRow? SelectedRow { get; set; }

    /// <summary>Set when the order could not be written, so the page says so.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string Problem { get; private set; } = string.Empty;

    /// <summary>Whether <see cref="Problem"/> has anything to say.</summary>
    public bool HasProblem => Problem.Length > 0;

    /// <summary>Whether nothing reports a battery, which the page explains rather than leaving blank.</summary>
    public bool HasNoBatterySources => Rows.Count == 0;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
    }

    /// <summary>Moves the selected row one place towards the top.</summary>
    [RelayCommand]
    private void MoveUp() => Move(-1);

    /// <summary>Moves the selected row one place towards the bottom.</summary>
    [RelayCommand]
    private void MoveDown() => Move(1);

    private void Move(int offset)
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var from = Rows.IndexOf(row);
        var to = from + offset;

        if (from < 0 || to < 0 || to >= Rows.Count)
        {
            return;
        }

        Rows.Move(from, to);

        // What is on screen is written first, so the persisted value leads with exactly what the user
        // is looking at, and sources they never ranked get ranked the moment they touch anything -
        // which makes a partially-ranked file impossible. Merged rather than written wholesale
        // because Rows holds only sources reporting a battery right now: an identifier belonging to
        // a disabled plugin, or to a device that has been switched off since startup and so has never
        // declared a battery at all, is not on screen to be written and would be dropped.
        var config = _configStore.Load();
        config.App.BatteryDisplayOrder = BatteryOrdering.Merge(
            Rows.Select(item => item.SourceId).ToList(),
            config.App.BatteryDisplayOrder);

        var saved = false;

        try
        {
            _configStore.Save(config);
            saved = true;
            Problem = string.Empty;
        }
        catch (Exception ex)
        {
            // Save is documented as deliberately not failing soft, precisely so this can be shown.
            Problem = $"The battery order could not be saved: {ex.Message}";
        }

        // Only on a successful save: if it threw, the on-disk order is unchanged, so the shell would
        // just re-read exactly what it already has. Nudging here matters because the shell otherwise
        // only re-reads on its own 30-second poll, which is the lag this exists to close.
        if (saved)
        {
            try
            {
                _notifyTrayOrderChanged();
            }
            catch (Exception ex)
            {
                // Separate from the save's catch, and worded as a different failure: the order is
                // already on disk, and the user must not be told a reorder they made was discarded
                // because the icon failed to redraw.
                Problem = $"The battery order was saved, but the notification-area icon could not be updated: {ex.Message}";
            }
        }

        // Selection survives because the row object moved rather than being replaced.
        SelectedRow = row;
        RefreshTrayPick();
    }

    private void Refresh()
    {
        var preferred = _configStore.Load().App.BatteryDisplayOrder;
        Reconcile(BatteryOrdering.Order(_engine.SourceStates(), preferred));
        RefreshTrayPick();
    }

    /// <summary>
    /// Bring the list into line with the engine, reusing the existing row objects.
    /// </summary>
    /// <remarks>
    /// Reconciled by identifier rather than rebuilt, the way the Rules page does it and for the same
    /// reason: refilling the collection every two seconds would replace the object the
    /// <c>ListView</c> holds as its selection and drop it out from under a user mid-reorder.
    /// </remarks>
    private void Reconcile(IReadOnlyList<SourceState> ordered)
    {
        for (var index = 0; index < ordered.Count; index++)
        {
            var state = ordered[index];
            var id = state.Descriptor.SourceId;

            var existing = Rows.FirstOrDefault(
                item => string.Equals(item.SourceId, id, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                existing = new BatteryRow(id);
                Rows.Insert(index, existing);
            }
            else
            {
                var currentIndex = Rows.IndexOf(existing);
                if (currentIndex != index)
                {
                    Rows.Move(currentIndex, index);
                }
            }

            existing.Name = state.Descriptor.DisplayName;
            existing.Detail = state.Descriptor.Detail ?? string.Empty;
            existing.Level = BatteryOrdering.Format(state.Battery);
            existing.State = BatteryOrdering.StateFor(state.Presence);
        }

        while (Rows.Count > ordered.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }

        OnPropertyChanged(nameof(HasNoBatterySources));
    }

    private void RefreshTrayPick()
    {
        var preferred = Rows.Select(row => row.SourceId).ToList();
        var pick = BatteryOrdering.PickForTray(BatteryOrdering.Order(_engine.SourceStates(), preferred));

        foreach (var row in Rows)
        {
            row.IsTrayPick = pick is not null
                && string.Equals(row.SourceId, pick.Descriptor.SourceId, StringComparison.OrdinalIgnoreCase);
        }
    }

}
