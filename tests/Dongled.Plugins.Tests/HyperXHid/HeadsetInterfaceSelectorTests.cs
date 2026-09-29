using Dongled.Plugin.HyperXHid;
using Xunit;

namespace Dongled.Plugins.Tests.HyperXHid;

public sealed class HeadsetInterfaceSelectorTests
{
    [Fact]
    public void The_real_dongles_six_interfaces_select_the_sixty_four_byte_one()
    {
        // Measured on a Cloud III S: six interfaces at VID 03F0 / PID 06BE with these input report
        // lengths. Only the largest carries the event reports.
        Assert.Equal(0, HeadsetInterfaceSelector.SelectLargestInputReport([64, 8, 2, 32, 62, 2]));
    }

    [Fact]
    public void The_largest_wins_wherever_it_sits()
    {
        Assert.Equal(2, HeadsetInterfaceSelector.SelectLargestInputReport([8, 2, 64, 32]));
        Assert.Equal(3, HeadsetInterfaceSelector.SelectLargestInputReport([2, 8, 32, 64]));
    }

    [Fact]
    public void An_interface_whose_length_could_not_be_read_is_skipped()
    {
        // The transport answers -1 when the device refuses the query, and an interface with no input
        // report cannot deliver an event.
        Assert.Equal(1, HeadsetInterfaceSelector.SelectLargestInputReport([-1, 8]));
        Assert.Equal(1, HeadsetInterfaceSelector.SelectLargestInputReport([0, 8]));
    }

    [Fact]
    public void Nothing_usable_selects_nothing_rather_than_falling_back_to_the_first()
    {
        // No falling back to the first interface. An interface with no input report is not a fallback, it is a device that cannot answer; opening it wastes a session and
        // reports a false absence.
        Assert.Null(HeadsetInterfaceSelector.SelectLargestInputReport([0, -1, 0]));
        Assert.Null(HeadsetInterfaceSelector.SelectLargestInputReport([]));
    }

    [Fact]
    public void A_tie_takes_the_first_so_the_choice_is_stable_across_scans()
    {
        Assert.Equal(0, HeadsetInterfaceSelector.SelectLargestInputReport([64, 64]));
    }
}
