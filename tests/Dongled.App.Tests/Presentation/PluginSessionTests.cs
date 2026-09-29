using Dongled.App.Presentation;
using Xunit;

namespace Dongled.App.Tests.Presentation;

public class PluginSessionTests
{
    [Fact]
    public void Nothing_needs_a_restart_until_something_asks_for_one()
    {
        Assert.False(new PluginSession().NeedsRestart);
    }

    [Fact]
    public void A_restart_stays_needed_once_it_has_been_asked_for()
    {
        // The reason this lives on the session rather than the view model: the page builds a new
        // view model on every navigation, so a flag held there would take the restart offer away
        // the moment the user glanced at another page - while the row went on saying the plugin
        // loads on the next start. Nothing clears it, because only restarting resolves it.
        var session = new PluginSession();

        session.RecordNeedsRestart();
        session.RecordInstalled("Later", "abc");
        session.RecordRemoved("Later");

        Assert.True(session.NeedsRestart);
    }

    [Fact]
    public void A_fresh_session_has_nothing_to_report()
    {
        var session = new PluginSession();

        Assert.Empty(session.Installed);
        Assert.Empty(session.Removed);
        Assert.Null(session.InstalledSha256Of("Sample"));
    }

    [Fact]
    public void Installing_a_directory_records_the_hash_the_installer_verified()
    {
        var session = new PluginSession();

        session.RecordInstalled("Sample", "abc123");

        Assert.Equal(["Sample"], session.Installed);
        Assert.Equal("abc123", session.InstalledSha256Of("Sample"));
    }

    [Fact]
    public void Installing_a_directory_takes_it_out_of_the_removed_list()
    {
        // PluginRows.Order requires the two lists to be mutually exclusive, because sets carry no
        // order and it cannot tell "removed, then installed" from "installed, then removed". This
        // is the half of that contract a user hits by removing a plugin and putting it back.
        var session = new PluginSession();

        session.RecordRemoved("Sample");
        session.RecordInstalled("Sample", "abc123");

        Assert.Equal(["Sample"], session.Installed);
        Assert.Empty(session.Removed);
    }

    [Fact]
    public void Removing_a_directory_takes_it_out_of_the_installed_list()
    {
        // The other half, and the one a user hits by installing something and changing their mind.
        var session = new PluginSession();

        session.RecordInstalled("Sample", "abc123");
        session.RecordRemoved("Sample");

        Assert.Equal(["Sample"], session.Removed);
        Assert.Empty(session.Installed);
        Assert.Null(session.InstalledSha256Of("Sample"));
    }

    [Fact]
    public void Directory_names_are_matched_the_way_Windows_matches_them()
    {
        // One folder must never occupy two rows, and the file system that named it does not
        // distinguish "Sample" from "SAMPLE".
        var session = new PluginSession();

        session.RecordInstalled("Sample", "abc123");

        Assert.Equal(["Sample"], session.Installed);
        Assert.Equal("abc123", session.InstalledSha256Of("SaMpLe"));

        session.RecordRemoved("sAmPlE");

        Assert.Empty(session.Installed);
        Assert.Equal(["sAmPlE"], session.Removed);
    }

    [Fact]
    public void Installing_the_same_directory_twice_lists_it_once_with_the_newer_hash()
    {
        var session = new PluginSession();

        session.RecordInstalled("Sample", "abc123");
        session.RecordInstalled("SAMPLE", "def456");

        Assert.Equal(["Sample"], session.Installed);
        Assert.Equal("def456", session.InstalledSha256Of("Sample"));
    }
}
