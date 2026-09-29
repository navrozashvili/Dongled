using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dongled.App.Presentation;
using Dongled.App.Services;
using Dongled.Core.Audio;
using Dongled.Core.Configuration;
using Dongled.Core.Engine;

namespace Dongled.App.ViewModels;

/// <summary>
/// The Rules page: the list of rules, the selected rule's <see cref="Editor"/>, and saving.
/// </summary>
/// <remarks>
/// Edits are saved as they are made, so there is no Save button and no way to lose work by
/// selecting another rule or leaving the page.
/// </remarks>
internal sealed partial class RulesViewModel : ObservableObject, IDisposable
{
    private readonly IConfigStore _configStore;
    private readonly IAudioEndpointService _endpoints;
    private readonly ISwitchingEngine _engine;
    private readonly RuleChoiceLists _choices = new();

    private AppConfig _config;
    private RuleListItem? _selectedRule;
    private int _stabilizationSeconds;
    private bool _disposed;

    /// <param name="configStore">Where rules are read and written.</param>
    /// <param name="endpoints">Windows audio, for the device dropdowns.</param>
    /// <param name="engine">Asked what sources exist, and told to re-read configuration after a save.</param>
    /// <param name="dispatcher">The UI thread, which hosts the editor's typing timer.</param>
    public RulesViewModel(
        IConfigStore configStore,
        IAudioEndpointService endpoints,
        ISwitchingEngine engine,
        IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _configStore = configStore;
        _endpoints = endpoints;
        _engine = engine;

        Editor = new RuleEditorViewModel(_choices, dispatcher, SaveSelectedRule);

        _config = configStore.Load();
        _stabilizationSeconds = _config.App.DisconnectStabilizationSeconds;


        RefreshChoices();
        RefreshList();
    }

    /// <summary>The rules, in configuration order.</summary>
    public ObservableCollection<RuleListItem> Rules { get; } = [];

    /// <summary>The selected rule's fields.</summary>
    public RuleEditorViewModel Editor { get; }

    /// <summary>The rule being edited, or null when none is selected.</summary>
    public RuleListItem? SelectedRule
    {
        get => _selectedRule;
        set
        {
            // Before the selection moves, or text typed into this rule would be saved into the next.
            Editor.FlushPendingText();

            if (SetProperty(ref _selectedRule, value))
            {
                LoadEditor();
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasNoRules));
            }
        }
    }

    /// <summary>Whether a rule is selected, which is what shows the editor.</summary>
    public bool HasSelection => _selectedRule is not null;

    /// <summary>Whether there are no rules at all, which the page explains.</summary>
    public bool HasNoRules => Rules.Count == 0;

    /// <summary>How long to wait after a disconnect before acting on it.</summary>
    /// <remarks>
    /// One setting for every rule, although the editor shows it inside one rule's sentence; the
    /// page says so beside it.
    /// </remarks>
    public int StabilizationSeconds
    {
        get => _stabilizationSeconds;
        set
        {
            if (SetProperty(ref _stabilizationSeconds, value))
            {
                SaveSelectedRule();
            }
        }
    }

    /// <summary>Set when reading devices or saving failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string Problem { get; private set; } = string.Empty;

    /// <summary>Whether <see cref="Problem"/> has anything to say.</summary>
    public bool HasProblem => Problem.Length > 0;

    /// <summary>What is wrong with the selected rule as it stands, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidation))]
    public partial string Validation { get; private set; } = string.Empty;

    /// <summary>Whether <see cref="Validation"/> has anything to say.</summary>
    public bool HasValidation => Validation.Length > 0;

    /// <inheritdoc />
    /// <remarks>Saves anything still waiting for typing to pause rather than discarding it.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Editor.FlushPendingText();
        Editor.Dispose();
    }

    /// <summary>
    /// Re-read what sources and devices exist, so that one switched on while the page is open
    /// appears in the dropdowns.
    /// </summary>
    public void RefreshChoices()
    {
        IReadOnlyList<AudioEndpoint> endpoints = [];

        try
        {
            endpoints = _endpoints.Enumerate();
            Problem = string.Empty;
        }
        catch (Exception ex)
        {
            // An empty list would silently offer nothing to pick, so the reason is shown instead.
            Problem = $"The list of playback devices could not be read: {ex.Message}";
        }

        var keep = Editor.SelectedIds;

        _choices.Rebuild(endpoints, _engine.SourceStates(), _config, keep);
        Editor.Reselect(keep);
        Editor.RefreshPatternStatus();
    }

    private Rule? CurrentRule() => _selectedRule is null
        ? null
        : _config.Rules.FirstOrDefault(rule => string.Equals(rule.Id, _selectedRule.Id, StringComparison.Ordinal));

    private void LoadEditor()
    {
        if (CurrentRule() is not { } rule)
        {
            return;
        }

        Editor.Load(rule, _config);
        SetProperty(ref _stabilizationSeconds, _config.App.DisconnectStabilizationSeconds, nameof(StabilizationSeconds));
        Validate();
    }

    /// <summary>Adds a rule and selects it.</summary>
    [RelayCommand]
    private void AddRule()
    {
        var rule = new Rule
        {
            // "N" format, 32 lowercase hexadecimal digits, as Rule.Id documents.
            Id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            Name = "New rule",
            Enabled = true,
        };

        _config.Rules.Add(rule);

        if (!Save())
        {
            _config.Rules.Remove(rule);
            return;
        }

        RefreshList();
        SelectedRule = Rules.FirstOrDefault(item => string.Equals(item.Id, rule.Id, StringComparison.Ordinal));
    }

    /// <summary>Deletes the selected rule.</summary>
    [RelayCommand]
    private void DeleteRule()
    {
        if (CurrentRule() is not { } rule)
        {
            return;
        }

        _config.Rules.Remove(rule);

        if (!Save())
        {
            // Put back, so the list does not disagree with the file.
            _config.Rules.Add(rule);
            return;
        }

        _selectedRule = null;
        RefreshList();
        OnPropertyChanged(nameof(SelectedRule));
        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>Write the editor's fields into the selected rule and save.</summary>
    private void SaveSelectedRule()
    {
        if (CurrentRule() is not { } rule)
        {
            return;
        }

        Editor.WriteTo(rule);
        _config.App.DisconnectStabilizationSeconds = _stabilizationSeconds;

        if (Save())
        {
            RefreshList();
        }

        Validate();
    }

    private bool Save()
    {
        try
        {
            _configStore.Save(_config);
            Problem = string.Empty;
        }
        catch (Exception ex)
        {
            Problem = $"The rules could not be saved: {ex.Message}";
            return false;
        }

        // Re-read what was actually written, so the page shows the store's corrections — a clamped
        // delay, for instance — rather than what was typed.
        _config = _configStore.Load();
        _stabilizationSeconds = _config.App.DisconnectStabilizationSeconds;
        OnPropertyChanged(nameof(StabilizationSeconds));

        _engine.RequestConfigurationReload();
        return true;
    }

    private void Validate() =>
        Validation = CurrentRule() is { } rule ? RuleValidation.Describe(rule, _config.Rules) : string.Empty;

    private void RefreshList()
    {
        RuleList.Reconcile(Rules, _config.Rules, _choices.Endpoints, _choices.SourceStates);
        OnPropertyChanged(nameof(HasNoRules));
    }
}
