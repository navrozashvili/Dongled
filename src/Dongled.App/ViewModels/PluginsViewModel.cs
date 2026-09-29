using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dongled.App.Presentation;
using Dongled.App.Services;
using Dongled.Core.Configuration;
using Dongled.Core.Engine;
using Dongled.Core.Plugins;

namespace Dongled.App.ViewModels;

/// <summary>
/// The Plugins page: one row per directory under the plugins root, the action each one calls for,
/// and the flows for adding, approving, switching off and removing plugins.
/// </summary>
/// <remarks>
/// <para>
/// The page decides nothing about whether a plugin is trustworthy. It lists every directory,
/// including the ones that did not load and the ones that are not plugins at all, so that the
/// user's choice is informed and can be undone.
/// </para>
/// <para>
/// Plugins are loaded once, at startup. Approving or switching one off changes configuration and
/// then offers a restart; nothing here loads or unloads code.
/// </para>
/// </remarks>
internal sealed partial class PluginsViewModel : ObservableObject, IPluginInstallFlowHost
{
    private readonly IPluginRuntime _runtime;
    private readonly IConfigStore _configStore;
    private readonly ISwitchingEngine _engine;
    private readonly TimeProvider _time;
    private readonly IPluginInstaller _installer;
    private readonly PluginSession _session;
    private readonly IUiDispatcher _dispatcher;
    private readonly IDialogService _dialogs;
    private readonly PluginRowCommands _rowCommands;
    private readonly PluginInstallFlow _installFlow;

    /// <summary>
    /// Whether a dialog or the file picker is already up. The picker is a separate window, so the
    /// page stays clickable behind it, and opening a second dialog while one is showing throws.
    /// </summary>
    private bool _busy;

    /// <param name="runtime">Where load results and lifecycle state come from.</param>
    /// <param name="configStore">Where approval and log level are recorded.</param>
    /// <param name="engine">Told to re-read configuration after a save.</param>
    /// <param name="time">Clock for the approval timestamp.</param>
    /// <param name="installer">Puts a package into the plugins directory, and takes one out again.</param>
    /// <param name="session">
    /// What has been installed and removed since the app started. Shared across visits to the page,
    /// because a new view model is built on every navigation.
    /// </param>
    /// <param name="dispatcher">The UI thread.</param>
    /// <param name="dialogs">Asks the user before each action.</param>
    /// <param name="requestRestart">Shuts this copy down and starts a fresh one.</param>
    public PluginsViewModel(
        IPluginRuntime runtime,
        IConfigStore configStore,
        ISwitchingEngine engine,
        TimeProvider time,
        IPluginInstaller installer,
        PluginSession session,
        IUiDispatcher dispatcher,
        IDialogService dialogs,
        Action requestRestart)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(requestRestart);

        _runtime = runtime;
        _configStore = configStore;
        _engine = engine;
        _time = time;
        _installer = installer;
        _session = session;
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _installFlow = new PluginInstallFlow(dialogs, installer, session, this);

        RestartCommand = new RelayCommand(requestRestart);
        _rowCommands = new PluginRowCommands(
            Approve: RowCommand(ApproveAsync),
            Disable: RowCommand(DisableAsync),
            Remove: RowCommand(RemoveAsync));

        // The listings are empty until loading has finished, so the page waits rather than showing
        // an empty list that suggests there are no plugins.
        if (_runtime.Started.IsCompleted)
        {
            IsLoading = false;
            Refresh();
        }
        else
        {
            WaitForStart();
        }
    }

    /// <summary>One row per directory under the plugins root.</summary>
    public ObservableCollection<PluginRow> Rows { get; } = [];

    /// <summary>Restarts the app so a plugin change takes effect.</summary>
    public ICommand RestartCommand { get; }

    /// <summary>Whether a change has been made that only a restart applies.</summary>
    /// <remarks>
    /// Kept on the session, which lives as long as the process, so that the offer survives
    /// navigating to another page and back.
    /// </remarks>
    public bool NeedsRestart => _session.NeedsRestart;

    /// <summary>Whether the host is still loading plugins.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoRows))]
    public partial bool IsLoading { get; private set; } = true;

    /// <summary>Whether there is nothing to list, once loading has finished.</summary>
    public bool HasNoRows => !IsLoading && Rows.Count == 0;

    /// <summary>Set when an action failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string Problem { get; private set; } = string.Empty;

    /// <summary>Whether <see cref="Problem"/> has anything to say.</summary>
    public bool HasProblem => Problem.Length > 0;

    /// <summary>What the last action did.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string Notice { get; private set; } = string.Empty;

    /// <summary>Whether <see cref="Notice"/> has anything to say.</summary>
    public bool HasNotice => Notice.Length > 0;

    /// <summary>Ask, then record approval for a row's directory and switch it on.</summary>
    /// <remarks>
    /// The one action that hands control of the process to code the user chose, so it always asks
    /// first and says plainly what approving means.
    /// </remarks>
    internal Task ApproveAsync(PluginRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return RunExclusiveAsync($"{row.Title} could not be enabled", async () =>
        {
            if (await _dialogs.ConfirmAsync(PluginPrompts.Approve(row.Title, row.Directory, row.CurrentSha256, justInstalled: false)))
            {
                Approve(row);
            }
        });
    }

    /// <summary>Ask, then switch a row's directory off while keeping its approval.</summary>
    internal Task DisableAsync(PluginRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return RunExclusiveAsync($"{row.Title} could not be switched off", async () =>
        {
            if (await _dialogs.ConfirmAsync(PluginPrompts.Disable(row.Title)))
            {
                Disable(row);
            }
        });
    }

    /// <summary>Ask, then delete a row's folder and forget everything recorded about it.</summary>
    internal Task RemoveAsync(PluginRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return RunExclusiveAsync($"{row.Title} could not be removed", async () =>
        {
            if (await _dialogs.ConfirmAsync(PluginPrompts.Remove(row.Title)))
            {
                Remove(row);
            }
        });
    }

    /// <summary>Picks a package, installs it and offers to approve it; see <see cref="PluginInstallFlow"/>.</summary>
    /// <remarks>
    /// Concurrent executions are allowed so the button never greys out: re-entry is refused by the
    /// one-flow-at-a-time guard every dialog-driven action shares, not by the command.
    /// </remarks>
    [RelayCommand(AllowConcurrentExecutions = true)]
    internal Task AddPluginAsync() =>
        RunExclusiveAsync("The plugin could not be added", _installFlow.RunAsync);

    /// <summary>
    /// A row action's command. Anything but a row as the parameter does nothing, and concurrent
    /// executions are allowed for the same reason as <see cref="AddPluginCommand"/>.
    /// </summary>
    private static AsyncRelayCommand<object?> RowCommand(Func<PluginRow, Task> action) =>
        new(
            parameter => parameter is PluginRow row ? action(row) : Task.CompletedTask,
            AsyncRelayCommandOptions.AllowConcurrentExecutions);

    /// <inheritdoc />
    void IPluginInstallFlowHost.ReportProblem(string problem)
    {
        Problem = problem;

        // Whatever the last action reported is not true of this one.
        Notice = string.Empty;
    }

    /// <inheritdoc />
    void IPluginInstallFlowHost.ReportNotice(string notice)
    {
        Problem = string.Empty;
        Notice = notice;
    }

    /// <inheritdoc />
    void IPluginInstallFlowHost.NotifyConfigurationChanged() => NotifyConfigurationChanged();

    /// <inheritdoc />
    void IPluginInstallFlowHost.Refresh() => Refresh();

    /// <inheritdoc />
    void IPluginInstallFlowHost.ApproveInstalled(string directory, string sha256, string title) =>
        ApproveInstalled(directory, sha256, title);

    /// <summary>
    /// Run one dialog-driven flow at a time, and turn anything unexpected it throws into a message
    /// rather than an unhandled exception.
    /// </summary>
    /// <remarks>
    /// These flows are started from commands, which cannot return a task, so an exception that got
    /// out of here would end the process.
    /// </remarks>
    private async Task RunExclusiveAsync(string failureTitle, Func<Task> flow)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;

        try
        {
            await flow();
        }
        catch (Exception ex)
        {
            await ReportUnexpectedAsync(failureTitle, ex);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Say that something failed in a way nothing here anticipated.</summary>
    /// <remarks>
    /// Swallows whatever showing the message throws: a dialog that refuses to open is one of the
    /// failures that gets here, and there is nothing left to try.
    /// </remarks>
    private async Task ReportUnexpectedAsync(string title, Exception ex)
    {
        try
        {
            await _dialogs.ShowMessageAsync(title, ex.Message);
        }
        catch (Exception)
        {
        }
    }

    private void Approve(PluginRow row)
    {
        Mutate(row.Directory, entry =>
        {
            entry.Enabled = true;

            // Null only if the folder could not be hashed, in which case the dialog would not let the
            // user confirm. An entry with no hash never matches, so it could not approve anything.
            entry.ManifestSha256 = row.CurrentSha256;
            entry.ApprovedUtc = _time.GetUtcNow();
        });

        RequireRestart();
        Notice = $"{row.Title} is approved. It loads on the next start — plugins are loaded once, at startup.";
    }

    /// <summary>Record approval for a directory the installer just wrote.</summary>
    /// <param name="directory">The folder that was installed.</param>
    /// <param name="sha256">The hash the installer computed there.</param>
    /// <param name="title">What to call it in the notice.</param>
    /// <remarks>
    /// Separate from <see cref="Approve"/> because a directory installed this session has no row
    /// built from a load result to take the hash from.
    /// </remarks>
    private void ApproveInstalled(string directory, string sha256, string title)
    {
        var saved = Mutate(directory, entry =>
        {
            entry.Enabled = true;
            entry.ManifestSha256 = sha256;
            entry.ApprovedUtc = _time.GetUtcNow();
        });

        // Checked because this goes on to refresh the rows: announcing an approval that was not
        // saved would contradict the error bar.
        if (!saved)
        {
            Notice = string.Empty;
            return;
        }

        RequireRestart();
        Notice = $"{title} is approved. It loads on the next start — plugins are loaded once, at startup.";
        Refresh();
    }

    /// <summary>Delete a plugin's folder and forget everything recorded about it.</summary>
    /// <remarks>
    /// Success means nothing of that name is installed or trusted any more, not necessarily that a
    /// folder was deleted, so the message comes from the installer, which knows what it found.
    /// </remarks>
    private void Remove(PluginRow row)
    {
        var result = _installer.Remove(row.Directory);

        if (!result.Succeeded)
        {
            Problem = result.Message;
            Notice = string.Empty;
            return;
        }

        Problem = string.Empty;

        _session.RecordRemoved(row.Directory);
        NotifyConfigurationChanged();

        Notice = result.Message;
        Refresh();
    }

    /// <summary>Switch a directory off, keeping its recorded approval.</summary>
    private void Disable(PluginRow row)
    {
        // The hash is kept: switching a plugin off is not withdrawing trust in its files, so
        // switching it back on should not require approving an unchanged folder again.
        Mutate(row.Directory, entry => entry.Enabled = false);

        RequireRestart();
        Notice = $"{row.Title} is switched off. It keeps running until the next start.";
    }

    /// <summary>Record a new minimum severity for one plugin. Takes effect immediately.</summary>
    private void UpdateLogLevel(PluginRow row) =>
        // A shipped plugin with no entry is loading without one. The entry this creates must not
        // switch it off, which a new entry otherwise would: Enabled defaults to false.
        Mutate(row.Directory, entry => entry.LogLevel = row.SelectedLogLevel, enableNewEntry: row.IsBundled);

    private void RequireRestart()
    {
        _session.RecordNeedsRestart();
        OnPropertyChanged(nameof(NeedsRestart));
    }

    private void WaitForStart() =>
        _ = _runtime.Started.ContinueWith(
            _ => _dispatcher.TryEnqueue(() =>
            {
                IsLoading = false;
                Refresh();
            }),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private void Refresh()
    {
        // Detached before the rows are dropped: a row can outlive the list, held by a dialog or
        // pushed a last value by a ComboBox being torn down, and must not save a log level for a
        // plugin the page no longer shows.
        foreach (var row in Rows)
        {
            row.LogLevelChanged -= OnRowLogLevelChanged;
        }

        Rows.Clear();

        var rows = PluginRowBuilder.Build(
            _runtime.Listings,
            _runtime.StateOf,
            _session,
            _configStore.Load(),
            _rowCommands);

        foreach (var row in rows)
        {
            row.LogLevelChanged += OnRowLogLevelChanged;
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(HasNoRows));
    }

    private void OnRowLogLevelChanged(object? sender, EventArgs e)
    {
        if (sender is PluginRow row)
        {
            UpdateLogLevel(row);
        }
    }

    /// <summary>Change one plugin's configuration entry and save it, adding the entry if there is none.</summary>
    /// <param name="directory">The plugin directory the entry is for.</param>
    /// <param name="change">The change to make.</param>
    /// <param name="enableNewEntry">Whether an entry created here starts switched on.</param>
    /// <returns>Whether the change was saved. If not, <see cref="Problem"/> says why.</returns>
    private bool Mutate(string directory, Action<PluginConfig> change, bool enableNewEntry = false)
    {
        try
        {
            var config = _configStore.Load();

            var entry = config.Plugins.FirstOrDefault(
                plugin => string.Equals(plugin.Directory, directory, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                entry = new PluginConfig { Directory = directory, Enabled = enableNewEntry };
                config.Plugins.Add(entry);
            }

            change(entry);

            _configStore.Save(config);

            Problem = string.Empty;

            NotifyConfigurationChanged();

            return true;
        }
        catch (Exception ex)
        {
            Problem = $"The change could not be saved: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Tell the engine and the plugin host that configuration changed on disk. Neither watches the
    /// file.
    /// </summary>
    private void NotifyConfigurationChanged()
    {
        _engine.RequestConfigurationReload();
        _runtime.ReloadConfiguration();
    }
}
