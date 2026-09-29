using Dongled.Abstractions;
using Dongled.App.Tests.Fakes;
using Dongled.App.ViewModels;
using Dongled.Core.Audio;
using Dongled.Core.Configuration;
using Dongled.Core.Pipeline;
using Xunit;

namespace Dongled.App.Tests.ViewModels;

public class RulesViewModelTests
{
    private const string HeadsetId = "{0.0.0.00000000}.{headset}";
    private const string SpeakersId = "{0.0.0.00000000}.{speakers}";
    private const string DockId = "{0.0.0.00000000}.{dock}";

    private readonly FakeConfigStore _store;
    private readonly FakeAudioEndpointService _endpoints = new();
    private readonly FakeSwitchingEngine _engine = new();
    private readonly ManualUiDispatcher _dispatcher = new();

    public RulesViewModelTests()
    {
        _endpoints.Endpoints.Add(new AudioEndpoint(HeadsetId, "Headset (HyperX)", true, false, false));
        _endpoints.Endpoints.Add(new AudioEndpoint(SpeakersId, "Speakers (Realtek)", true, true, true));
        _endpoints.Endpoints.Add(new AudioEndpoint(DockId, "Speakers (Dock)", false, false, false));

        _engine.Sources.Add(new SourceState(new AudioSourceDescriptor("hyperx:cloud", "HyperX Cloud III S", null), Presence.Present, null));

        _store = new FakeConfigStore(new AppConfig
        {
            Rules =
            [
                new Rule
                {
                    Id = "first",
                    Name = "Headset",
                    Source = new RuleSource { Id = "hyperx:cloud", LastKnownName = "HyperX Cloud III S" },
                    Target = new RuleTarget { DeviceId = HeadsetId, LastKnownName = "Headset (HyperX)" },
                    OnDisconnect = new DisconnectBehavior { FallbackDeviceId = SpeakersId, FallbackLastKnownName = "Speakers (Realtek)" },
                },
                new Rule
                {
                    Id = "second",
                    Name = "Dock",
                    Source = new RuleSource { Id = "hyperx:cloud" },
                    Target = new RuleTarget { DeviceId = SpeakersId },
                    Enabled = false,
                },
            ],
        });
    }

    private RulesViewModel Create() => new(_store, _endpoints, _engine, _dispatcher);

    private static RuleListItem Item(RulesViewModel viewModel, string id) => viewModel.Rules.Single(item => item.Id == id);

    private Rule StoredRule(string id) => _store.Stored.Rules.Single(rule => rule.Id == id);

    [Fact]
    public void The_list_shows_each_rule_by_name_with_a_summary_in_names()
    {
        using var viewModel = Create();

        Assert.Equal(["Headset", "Dock"], viewModel.Rules.Select(item => item.Name));
        Assert.Equal("When HyperX Cloud III S connects, make Headset (HyperX) the default", viewModel.Rules[0].Summary);
        Assert.EndsWith("(switched off)", viewModel.Rules[1].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Selecting_a_rule_shows_it_without_saving_anything()
    {
        using var viewModel = Create();

        viewModel.SelectedRule = Item(viewModel, "first");

        Assert.True(viewModel.HasSelection);
        Assert.Equal("Headset", viewModel.Editor.RuleName);
        Assert.Equal(HeadsetId, viewModel.Editor.SelectedTarget?.Id);
        Assert.Equal(SpeakersId, viewModel.Editor.SelectedFallback?.Id);
        Assert.True(viewModel.Editor.MediaSelected);
        Assert.True(viewModel.Editor.CallsSelected);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public void A_ticked_box_is_saved_at_once()
    {
        using var viewModel = Create();
        viewModel.SelectedRule = Item(viewModel, "first");

        viewModel.Editor.CallsSelected = false;

        Assert.Equal([AudioRole.Media], StoredRule("first").Target.Roles);
        Assert.Equal(1, _engine.ReloadRequests);
    }

    [Fact]
    public void A_typed_name_is_saved_once_typing_pauses()
    {
        using var viewModel = Create();
        viewModel.SelectedRule = Item(viewModel, "first");

        viewModel.Editor.RuleName = "H";
        viewModel.Editor.RuleName = "Headphones";

        Assert.Equal(0, _store.SaveCount);

        _dispatcher.ElapseAll();

        Assert.Equal(1, _store.SaveCount);
        Assert.Equal("Headphones", StoredRule("first").Name);
        Assert.Equal("Headphones", Item(viewModel, "first").Name);
    }

    [Fact]
    public void A_name_still_being_typed_is_saved_to_its_own_rule_when_the_selection_moves()
    {
        using var viewModel = Create();
        viewModel.SelectedRule = Item(viewModel, "first");
        viewModel.Editor.RuleName = "Headphones";

        viewModel.SelectedRule = Item(viewModel, "second");

        Assert.Equal("Headphones", StoredRule("first").Name);
        Assert.Equal("Dock", StoredRule("second").Name);
        Assert.Equal("Dock", viewModel.Editor.RuleName);
    }

    [Fact]
    public void Leaving_the_page_saves_a_name_still_being_typed()
    {
        var viewModel = Create();
        viewModel.SelectedRule = Item(viewModel, "first");
        viewModel.Editor.RuleName = "Headphones";

        viewModel.Dispose();

        Assert.Equal("Headphones", StoredRule("first").Name);
        Assert.All(_dispatcher.Timers, timer => Assert.True(timer.IsDisposed));
    }

    [Fact]
    public void Renaming_updates_the_listed_item_in_place()
    {
        // Replacing the item would make the list reassign its selection and take focus out of the
        // name box on every save.
        using var viewModel = Create();
        var item = Item(viewModel, "first");
        viewModel.SelectedRule = item;

        viewModel.Editor.RuleName = "Headphones";
        _dispatcher.ElapseAll();

        Assert.Same(item, Item(viewModel, "first"));
        Assert.Same(item, viewModel.SelectedRule);
    }

    [Fact]
    public void A_disconnected_device_is_offered_with_a_qualifier_but_stored_by_its_bare_name()
    {
        using var viewModel = Create();
        viewModel.SelectedRule = Item(viewModel, "first");

        var dock = viewModel.Editor.DeviceChoices.Single(choice => choice.Id == DockId);
        Assert.Equal("Speakers (Dock) — not currently connected", dock.Label);

        viewModel.Editor.SelectedTarget = dock;

        Assert.Equal(DockId, StoredRule("first").Target.DeviceId);
        Assert.Equal("Speakers (Dock)", StoredRule("first").Target.LastKnownName);
    }

    [Fact]
    public void A_device_that_no_longer_exists_stays_selected_under_its_last_known_name()
    {
        _endpoints.Endpoints.RemoveAll(endpoint => endpoint.Id == HeadsetId);

        using var viewModel = Create();
        viewModel.SelectedRule = Item(viewModel, "first");

        Assert.Equal(HeadsetId, viewModel.Editor.SelectedTarget?.Id);
        Assert.Equal("Headset (HyperX)", viewModel.Editor.SelectedTarget?.Name);

        // Editing something else must not repoint the rule at another device.
        viewModel.Editor.MediaSelected = false;

        Assert.Equal(HeadsetId, StoredRule("first").Target.DeviceId);
        Assert.Equal("Headset (HyperX)", StoredRule("first").Target.LastKnownName);
    }

    [Fact]
    public void Matching_by_name_starts_from_the_picked_device()
    {
        using var viewModel = Create();
        viewModel.SelectedRule = Item(viewModel, "first");

        viewModel.Editor.TargetMatch.UsesPattern = true;

        var pattern = viewModel.Editor.TargetMatch.Pattern;
        Assert.Equal(DeviceNameMatcher.PatternFor("Headset (HyperX)"), pattern);
        Assert.Equal(pattern, StoredRule("first").Target.NamePattern);
        Assert.Equal("Matches Headset (HyperX).", viewModel.Editor.TargetMatch.Status);
    }

    [Fact]
    public void Picking_another_device_while_matching_by_name_fills_the_pattern_in_again()
    {
        using var viewModel = Create();
        viewModel.SelectedRule = Item(viewModel, "first");
        viewModel.Editor.TargetMatch.UsesPattern = true;

        viewModel.Editor.SelectedTarget = viewModel.Editor.DeviceChoices.Single(choice => choice.Id == SpeakersId);

        Assert.Equal(DeviceNameMatcher.PatternFor("Speakers (Realtek)"), StoredRule("first").Target.NamePattern);
    }

    [Fact]
    public void An_unusable_pattern_is_not_saved_and_the_last_usable_one_is_kept()
    {
        using var viewModel = Create();
        viewModel.SelectedRule = Item(viewModel, "first");
        viewModel.Editor.TargetMatch.UsesPattern = true;
        var usable = StoredRule("first").Target.NamePattern;

        viewModel.Editor.TargetMatch.Pattern = "(unclosed";
        _dispatcher.ElapseAll();

        Assert.Equal(usable, StoredRule("first").Target.NamePattern);
        Assert.StartsWith("This pattern can't be used", viewModel.Editor.TargetMatch.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Turning_matching_off_clears_the_stored_pattern_at_once()
    {
        using var viewModel = Create();
        viewModel.SelectedRule = Item(viewModel, "first");
        viewModel.Editor.TargetMatch.UsesPattern = true;

        viewModel.Editor.TargetMatch.UsesPattern = false;

        Assert.Null(StoredRule("first").Target.NamePattern);
        Assert.Equal(string.Empty, viewModel.Editor.TargetMatch.Status);
    }

    [Fact]
    public void Choosing_no_fallback_stores_none()
    {
        using var viewModel = Create();
        viewModel.SelectedRule = Item(viewModel, "first");

        viewModel.Editor.SelectedFallback = viewModel.Editor.FallbackChoices[0];

        Assert.Null(StoredRule("first").OnDisconnect.FallbackDeviceId);
        Assert.Null(StoredRule("first").OnDisconnect.FallbackLastKnownName);
        Assert.Contains("nothing will happen", viewModel.Validation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_disconnect_mode_index_maps_to_the_modes_in_page_order()
    {
        using var viewModel = Create();
        viewModel.SelectedRule = Item(viewModel, "first");

        viewModel.Editor.DisconnectModeIndex = 3;

        Assert.Equal(DisconnectMode.DoNothing, StoredRule("first").OnDisconnect.Mode);
        Assert.False(viewModel.Editor.FallbackApplies);
    }

    [Fact]
    public void Two_switched_on_rules_on_one_source_are_warned_about()
    {
        using var viewModel = Create();
        viewModel.SelectedRule = Item(viewModel, "second");
        Assert.DoesNotContain("Another switched-on rule", viewModel.Validation, StringComparison.Ordinal);

        viewModel.Editor.RuleEnabled = true;

        Assert.Contains("Another switched-on rule watches the same source", viewModel.Validation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_disconnect_wait_is_saved_for_every_rule()
    {
        using var viewModel = Create();
        viewModel.SelectedRule = Item(viewModel, "first");

        viewModel.StabilizationSeconds = 12;

        Assert.Equal(12, _store.Stored.App.DisconnectStabilizationSeconds);
    }

    [Fact]
    public void Adding_a_rule_saves_it_and_selects_it()
    {
        using var viewModel = Create();

        viewModel.AddRuleCommand.Execute(null);

        Assert.Equal(3, _store.Stored.Rules.Count);
        Assert.Equal("New rule", viewModel.SelectedRule?.Name);
        Assert.Equal("New rule", viewModel.Editor.RuleName);
        Assert.Contains("Choose which source this rule watches.", viewModel.Validation, StringComparison.Ordinal);
    }

    [Fact]
    public void Deleting_the_selected_rule_removes_it_and_clears_the_selection()
    {
        using var viewModel = Create();
        viewModel.SelectedRule = Item(viewModel, "first");

        viewModel.DeleteRuleCommand.Execute(null);

        Assert.Equal(["second"], _store.Stored.Rules.Select(rule => rule.Id));
        Assert.Null(viewModel.SelectedRule);
        Assert.False(viewModel.HasSelection);
        Assert.Equal(["second"], viewModel.Rules.Select(item => item.Id));
    }

    [Fact]
    public void A_failed_save_is_reported_and_an_added_rule_is_taken_back()
    {
        using var viewModel = Create();
        _store.SaveFailure = new IOException("disk full");

        viewModel.AddRuleCommand.Execute(null);

        Assert.Equal("The rules could not be saved: disk full", viewModel.Problem);
        Assert.Equal(2, viewModel.Rules.Count);
        Assert.Null(viewModel.SelectedRule);
    }

    [Fact]
    public void Unreadable_devices_are_reported_rather_than_offered_as_an_empty_list()
    {
        _endpoints.EnumerateFailure = new InvalidOperationException("audio service stopped");

        using var viewModel = Create();

        Assert.Equal("The list of playback devices could not be read: audio service stopped", viewModel.Problem);
    }
}
