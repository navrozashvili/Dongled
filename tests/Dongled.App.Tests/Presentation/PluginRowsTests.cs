using Dongled.App.Presentation;
using Xunit;

namespace Dongled.App.Tests.Presentation;

public class PluginRowsTests
{
    [Fact]
    public void With_nothing_installed_or_removed_the_startup_order_is_kept()
    {
        Assert.Equal(
            ["Alpha", "Beta"],
            PluginRows.Order(["Alpha", "Beta"], [], []));
    }

    [Fact]
    public void A_directory_installed_this_session_is_appended_because_the_host_cannot_know_about_it_yet()
    {
        // PluginHost.Results is filled once, at startup, because loading is deliberately
        // startup-only. Nothing installed since can be in it, so the page has to add it itself.
        Assert.Equal(
            ["Alpha", "Beta", "Gamma"],
            PluginRows.Order(["Alpha", "Beta"], ["Gamma"], []));
    }

    [Fact]
    public void A_directory_removed_this_session_is_dropped()
    {
        Assert.Equal(
            ["Alpha"],
            PluginRows.Order(["Alpha", "Beta"], [], ["Beta"]));
    }

    [Fact]
    public void Reinstalling_over_a_directory_that_was_already_listed_does_not_list_it_twice()
    {
        Assert.Equal(
            ["Alpha", "Beta"],
            PluginRows.Order(["Alpha", "Beta"], ["Beta"], []));
    }

    [Fact]
    public void Installing_after_removing_the_same_name_brings_it_back_where_it_was()
    {
        // Upgrading is a remove and an install of one name, so this is the ordinary path rather
        // than an edge case, and the row staying put is what stops a list reordering itself under
        // the user each time they replace something.
        Assert.Equal(
            ["Alpha", "Beta"],
            PluginRows.Order(["Alpha", "Beta"], ["Beta"], ["Beta"]));
    }

    [Fact]
    public void Directory_names_are_matched_the_way_Windows_matches_them()
    {
        Assert.Equal(
            ["Alpha"],
            PluginRows.Order(["Alpha", "Beta"], [], ["BETA"]));
    }

    [Fact]
    public void A_name_installed_and_removed_that_startup_never_saw_is_gone_rather_than_appended()
    {
        // The reverse of the case above, and the reason the two lists have to be kept mutually
        // exclusive by whoever fills them: with only sets to go on this function cannot tell
        // "installed, then removed" from "removed, then installed", and appending a row for a
        // folder that is no longer on disk is the worse of the two ways to be wrong.
        Assert.Equal(
            ["Alpha"],
            PluginRows.Order(["Alpha"], ["Gamma"], ["Gamma"]));
    }

    [Fact]
    public void The_lists_the_caller_passes_are_left_alone()
    {
        string[] fromStartup = ["Alpha"];
        string[] installed = ["Beta"];
        string[] removed = ["Alpha"];

        PluginRows.Order(fromStartup, installed, removed);

        Assert.Equal(["Alpha"], fromStartup);
        Assert.Equal(["Beta"], installed);
        Assert.Equal(["Alpha"], removed);
    }

    [Fact]
    public void A_duplicate_in_the_installed_list_still_produces_one_row()
    {
        Assert.Equal(
            ["Gamma"],
            PluginRows.Order([], ["Gamma", "GAMMA"], []));
    }
}
