using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dongled.App.Mvvm;
using Dongled.App.Services;
using Dongled.Core.Configuration;

namespace Dongled.App.ViewModels;

/// <summary>
/// The fields of the rule being edited, laid out as a sentence: when this connects, make that the
/// default, and when it disconnects, do this.
/// </summary>
/// <remarks>
/// <para>
/// Every dropdown offers <see cref="ChoiceItem"/>s, whose labels are display names; no identifier,
/// placeholder or enum name reaches the screen.
/// </para>
/// <para>
/// Changes are reported to the owner as they happen, which saves them. Typed text — the name and
/// the two patterns — is reported once typing pauses, so that a name is not written to disk on
/// every keystroke.
/// </para>
/// </remarks>
internal sealed class RuleEditorViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// How long typing must pause before typed text is saved: long enough to coalesce ordinary
    /// typing, short enough that a save is never in doubt.
    /// </summary>
    private static readonly TimeSpan TextSettleDelay = TimeSpan.FromMilliseconds(500);

    private readonly RuleChoiceLists _choices;
    private readonly Action _changed;
    private readonly Debouncer _textSave;

    private string _ruleName = string.Empty;
    private bool _ruleEnabled = true;
    private ChoiceItem? _selectedSource;
    private ChoiceItem? _selectedTarget;
    private ChoiceItem? _selectedFallback;
    private bool _mediaSelected;
    private bool _callsSelected;
    private int _disconnectModeIndex;

    /// <summary>Set while a rule is being shown, so that showing it is not mistaken for editing it.</summary>
    private bool _loading;

    /// <param name="choices">What the dropdowns offer.</param>
    /// <param name="dispatcher">Hosts the timer that waits for typing to pause.</param>
    /// <param name="changed">Saves the edited rule.</param>
    public RuleEditorViewModel(RuleChoiceLists choices, IUiDispatcher dispatcher, Action changed)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(changed);

        _choices = choices;
        _changed = changed;
        _textSave = new Debouncer(dispatcher, TextSettleDelay, changed);

        TargetMatch = new NamePatternField(() => _selectedTarget, () => _choices.Endpoints, OnChanged, OnTyped);
        FallbackMatch = new NamePatternField(() => _selectedFallback, () => _choices.Endpoints, OnChanged, OnTyped);
    }

    public ObservableCollection<ChoiceItem> SourceChoices => _choices.Sources;

    public ObservableCollection<ChoiceItem> DeviceChoices => _choices.Devices;

    public ObservableCollection<ChoiceItem> FallbackChoices => _choices.Fallbacks;

    /// <summary>The rule's name. Saved once typing pauses.</summary>
    public string RuleName
    {
        get => _ruleName;
        set
        {
            if (SetProperty(ref _ruleName, value))
            {
                OnTyped();
            }
        }
    }

    /// <summary>Whether the rule participates in switching.</summary>
    public bool RuleEnabled
    {
        get => _ruleEnabled;
        set
        {
            if (SetProperty(ref _ruleEnabled, value))
            {
                OnChanged();
            }
        }
    }

    /// <summary>The source this rule watches.</summary>
    public ChoiceItem? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (SetProperty(ref _selectedSource, value))
            {
                OnChanged();
            }
        }
    }

    /// <summary>The device this rule switches to, and the one preferred when a pattern matches several.</summary>
    public ChoiceItem? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!SetProperty(ref _selectedTarget, value))
            {
                return;
            }

            if (!_loading)
            {
                TargetMatch.FillFrom(value);
            }

            TargetMatch.RefreshStatus();
            OnChanged();
        }
    }

    /// <summary>The device used when the disconnect mode calls for a fallback.</summary>
    public ChoiceItem? SelectedFallback
    {
        get => _selectedFallback;
        set
        {
            if (!SetProperty(ref _selectedFallback, value))
            {
                return;
            }

            if (!_loading)
            {
                FallbackMatch.FillFrom(value);
            }

            FallbackMatch.RefreshStatus();
            OnChanged();
        }
    }

    /// <summary>Matching the target by name.</summary>
    public NamePatternField TargetMatch { get; }

    /// <summary>Matching the fallback by name.</summary>
    public NamePatternField FallbackMatch { get; }

    /// <summary>Whether the rule switches the Media role.</summary>
    public bool MediaSelected
    {
        get => _mediaSelected;
        set
        {
            if (SetProperty(ref _mediaSelected, value))
            {
                OnChanged();
            }
        }
    }

    /// <summary>Whether the rule switches the Calls role.</summary>
    public bool CallsSelected
    {
        get => _callsSelected;
        set
        {
            if (SetProperty(ref _callsSelected, value))
            {
                OnChanged();
            }
        }
    }

    /// <summary>
    /// What happens on disconnect, as an index into the four modes in the order the page lists them.
    /// </summary>
    public int DisconnectModeIndex
    {
        get => _disconnectModeIndex;
        set
        {
            if (SetProperty(ref _disconnectModeIndex, value))
            {
                OnPropertyChanged(nameof(FallbackApplies));
                OnChanged();
            }
        }
    }

    /// <summary>Whether the chosen disconnect mode ever uses the fallback device.</summary>
    public bool FallbackApplies =>
        SelectedMode() is DisconnectMode.RestorePreviousElseFallback or DisconnectMode.AlwaysFallback;

    /// <summary>The identifiers currently picked in the three dropdowns.</summary>
    public RuleChoiceIds SelectedIds => new(_selectedSource?.Id, _selectedTarget?.Id, _selectedFallback?.Id);

    /// <summary>Show <paramref name="rule"/> without treating it as an edit.</summary>
    /// <param name="rule">The rule to show.</param>
    /// <param name="config">Configuration, for naming sources and devices that no longer exist.</param>
    public void Load(Rule rule, AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(rule);

        _loading = true;

        RuleName = rule.Name;
        RuleEnabled = rule.Enabled;

        _choices.EnsurePresent(RuleChoiceIds.Of(rule), config);

        SelectedSource = _choices.FindSource(rule.Source.Id);
        SelectedTarget = _choices.FindDevice(rule.Target.DeviceId);
        SelectedFallback = _choices.FindFallback(rule.OnDisconnect.FallbackDeviceId);

        TargetMatch.Load(rule.Target.NamePattern);
        FallbackMatch.Load(rule.OnDisconnect.FallbackNamePattern);

        MediaSelected = rule.Target.Roles.Contains(AudioRole.Media);
        CallsSelected = rule.Target.Roles.Contains(AudioRole.Calls);
        DisconnectModeIndex = IndexOf(rule.OnDisconnect.Mode);

        _loading = false;

        RefreshPatternStatus();
    }

    /// <summary>Pick the given identifiers again after the dropdown lists were rebuilt.</summary>
    public void Reselect(RuleChoiceIds ids)
    {
        _loading = true;

        SelectedSource = _choices.FindSource(ids.Source);
        SelectedTarget = _choices.FindDevice(ids.Target);
        SelectedFallback = _choices.FindFallback(ids.Fallback);

        _loading = false;
    }

    /// <summary>Recompute both pattern status lines, after the list of devices changed.</summary>
    public void RefreshPatternStatus()
    {
        TargetMatch.RefreshStatus();
        FallbackMatch.RefreshStatus();
    }

    /// <summary>Write the editor's fields into <paramref name="rule"/>.</summary>
    public void WriteTo(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        rule.Name = _ruleName;
        rule.Enabled = _ruleEnabled;

        // Names, never labels: a label may carry "— not currently connected", which must not be
        // stored.
        rule.Source.Id = _selectedSource?.Id ?? string.Empty;
        rule.Source.LastKnownName = _selectedSource?.Name;

        rule.Target.DeviceId = _selectedTarget?.Id ?? string.Empty;
        rule.Target.LastKnownName = _selectedTarget?.Name;
        rule.Target.NamePattern = TargetMatch.PatternToStore(rule.Target.NamePattern);

        var roles = new List<AudioRole>();
        if (_mediaSelected)
        {
            roles.Add(AudioRole.Media);
        }

        if (_callsSelected)
        {
            roles.Add(AudioRole.Calls);
        }

        rule.Target.Roles = roles;

        rule.OnDisconnect.Mode = SelectedMode();

        var fallbackId = _selectedFallback?.Id ?? string.Empty;
        rule.OnDisconnect.FallbackDeviceId = fallbackId.Length == 0 ? null : fallbackId;
        rule.OnDisconnect.FallbackLastKnownName = fallbackId.Length == 0 ? null : _selectedFallback?.Name;
        rule.OnDisconnect.FallbackNamePattern = FallbackMatch.PatternToStore(rule.OnDisconnect.FallbackNamePattern);
    }

    /// <summary>
    /// Save typed text now if it is waiting for typing to pause. Called before the selection moves,
    /// so that text typed into one rule is not saved into the next.
    /// </summary>
    public void FlushPendingText() => _textSave.Flush();

    /// <summary>Stop waiting for typing to pause. Call <see cref="FlushPendingText"/> first to keep it.</summary>
    public void Dispose() => _textSave.Dispose();

    private static int IndexOf(DisconnectMode mode) => mode switch
    {
        DisconnectMode.AlwaysFallback => 1,
        DisconnectMode.RestorePrevious => 2,
        DisconnectMode.DoNothing => 3,
        _ => 0,
    };

    private DisconnectMode SelectedMode() => _disconnectModeIndex switch
    {
        1 => DisconnectMode.AlwaysFallback,
        2 => DisconnectMode.RestorePrevious,
        3 => DisconnectMode.DoNothing,
        _ => DisconnectMode.RestorePreviousElseFallback,
    };

    private void OnChanged()
    {
        if (!_loading)
        {
            _changed();
        }
    }

    private void OnTyped()
    {
        if (!_loading)
        {
            _textSave.Trigger();
        }
    }
}
