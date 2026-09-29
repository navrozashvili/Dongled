using Dongled.Core.Configuration;
using Xunit;

namespace Dongled.Core.Tests.Configuration;

public class DomainModelTests
{
    [Fact]
    public void A_new_config_matches_the_documented_defaults()
    {
        var config = new AppConfig();

        Assert.Equal(1, config.SchemaVersion);
        Assert.False(config.App.StartWithWindows);
        Assert.True(config.App.ApplyRulesOnStartup);
        Assert.Equal(5, config.App.DisconnectStabilizationSeconds);
        Assert.Equal(AppTheme.System, config.App.Theme);
        Assert.Empty(config.Rules);
        Assert.Empty(config.Plugins);
    }

    [Fact]
    public void A_new_rule_targets_both_roles()
    {
        var target = new RuleTarget();

        Assert.Equal(new[] { AudioRole.Media, AudioRole.Calls }, target.Roles);
    }

    [Fact]
    public void The_default_disconnect_mode_restores_the_previous_device()
    {
        Assert.Equal(DisconnectMode.RestorePreviousElseFallback, new DisconnectBehavior().Mode);
    }

    [Fact]
    public void A_new_plugin_entry_is_disabled_and_defaults_to_warning()
    {
        // Deny by default. Warning in both Debug and Release, so bug reports from release
        // users match what a developer sees.
        var plugin = new PluginConfig();

        Assert.False(plugin.Enabled);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, plugin.LogLevel);
    }

    [Fact]
    public void A_freshly_constructed_state_matches_previous_defaults_case_insensitively()
    {
        // Endpoint identifiers are GUID-shaped strings whose casing Windows does not
        // guarantee to be stable across calls.
        //
        // Scope: this pins the initializer only. The comparer is NOT preserved across
        // deserialization; PreviousDefaults is settable, so System.Text.Json assigns a fresh
        // dictionary through the setter rather than populating this one, and the
        // ordinal-ignore-case comparer goes with it. The same assertion is false on a
        // round-tripped instance. Restoring the comparer after a load is the storage layer's
        // responsibility, not this type's.
        var state = new AppState();
        state.PreviousDefaults["{0.0.0.00000000}.{ABC}"] = new PreviousDefault("mm", "comm", DateTimeOffset.UnixEpoch);

        Assert.True(state.PreviousDefaults.ContainsKey("{0.0.0.00000000}.{abc}"));
    }

    [Fact]
    public void A_new_rule_is_enabled()
    {
        // Counterpart to the disabled-by-default plugin entry: both flags gate whether
        // something runs, so both are pinned.
        Assert.True(new Rule().Enabled);
    }

    [Fact]
    public void The_disconnect_mode_values_are_a_persisted_contract()
    {
        // These numbers are written to config.json. Renumbering or reordering them would
        // silently reinterpret every stored rule, so the values are asserted, not just the count.
        Assert.Equal(0, (int)DisconnectMode.RestorePreviousElseFallback);
        Assert.Equal(1, (int)DisconnectMode.AlwaysFallback);
        Assert.Equal(2, (int)DisconnectMode.RestorePrevious);
        Assert.Equal(3, (int)DisconnectMode.DoNothing);
        Assert.Equal(4, Enum.GetValues<DisconnectMode>().Length);
    }

    [Fact]
    public void The_audio_role_values_are_a_persisted_contract()
    {
        Assert.Equal(0, (int)AudioRole.Media);
        Assert.Equal(1, (int)AudioRole.Calls);
        Assert.Equal(2, Enum.GetValues<AudioRole>().Length);
    }
}
