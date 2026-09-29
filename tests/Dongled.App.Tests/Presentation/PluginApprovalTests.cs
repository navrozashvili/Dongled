using Dongled.App.Presentation;
using Xunit;

namespace Dongled.App.Tests.Presentation;

public class PluginApprovalTests
{
    private const string Installed = "A1B2C3";

    [Fact]
    public void A_directory_with_no_configuration_entry_is_not_approved()
    {
        // What a fresh install looks like: the installer writes files and records nothing, because
        // installing is not approving.
        Assert.Equal(
            PluginApprovalState.NotApproved,
            PluginApproval.StateOf(Installed, enabled: false, recordedSha256: null));
    }

    [Fact]
    public void A_recorded_hash_matching_the_installed_files_and_switched_on_is_approved()
    {
        Assert.Equal(
            PluginApprovalState.Approved,
            PluginApproval.StateOf(Installed, enabled: true, Installed));
    }

    [Fact]
    public void A_recorded_hash_matching_the_installed_files_and_switched_off_is_switched_off()
    {
        // The state that had no name before: approving an installed plugin and then switching it
        // off. Read as "not approved" it loses the Switch off button; read as "approved" it loses
        // the Enable button, which is how the row ended up with neither.
        Assert.Equal(
            PluginApprovalState.SwitchedOff,
            PluginApproval.StateOf(Installed, enabled: false, Installed));
    }

    [Fact]
    public void A_recorded_hash_for_different_files_is_not_approved()
    {
        // What an upgrade leaves behind if the entry is not cleared: approval covers files that are
        // no longer installed, and the loader would refuse it.
        Assert.Equal(
            PluginApprovalState.NotApproved,
            PluginApproval.StateOf(Installed, enabled: false, "D4E5F6"));
    }

    [Fact]
    public void A_recorded_hash_for_different_files_is_not_approved_even_when_switched_on()
    {
        // Not reachable through the app, because installing over a plugin clears the entry. It is
        // reachable by editing config.json, and the answer has to be the loader's answer: this will
        // not load, so offering to switch it off would describe something that is not happening.
        Assert.Equal(
            PluginApprovalState.NotApproved,
            PluginApproval.StateOf(Installed, enabled: true, "D4E5F6"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_recorded_hash_never_matches(string recorded)
    {
        // The property that makes an approval entry with no hash useless rather than permissive,
        // and the reason PluginManifest.Matches checks for blank before comparing.
        Assert.Equal(
            PluginApprovalState.NotApproved,
            PluginApproval.StateOf(Installed, enabled: true, recorded));
    }

    [Fact]
    public void Hashes_are_compared_without_regard_to_case()
    {
        // The value is uppercase hexadecimal from PluginManifest, but it has been out to config.json
        // and back, and PluginManifest.Matches compares it ignoring case. These two have to agree,
        // or the page calls a directory approved that the loader then refuses.
        Assert.Equal(
            PluginApprovalState.Approved,
            PluginApproval.StateOf(Installed, enabled: true, "a1b2c3"));
    }

    [Fact]
    public void An_installed_hash_is_required()
    {
        // There is no such thing as an installed directory without one: it is the value the
        // installer recomputed at the destination before reporting success.
        Assert.Throws<ArgumentException>(
            () => PluginApproval.StateOf("  ", enabled: true, Installed));
    }
}
