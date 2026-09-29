using Dongled.App.Presentation;
using Xunit;

namespace Dongled.App.Tests.Presentation;

public class WindowGeometryTests
{
    /// <summary>A 1080p display with a 40-pixel taskbar along the bottom.</summary>
    private static readonly WindowBounds Hd = new(0, 0, 1920, 1040);

    /// <summary>A 4K display at 150% scaling, which is what Windows picks for one by default.</summary>
    private static readonly WindowBounds Uhd = new(0, 0, 3840, 2100);

    private const double UhdScale = 1.5;

    [Fact]
    public void The_default_size_is_stated_in_dips_and_applied_in_pixels()
    {
        // The whole point of the class. 1150x780 at 150% is 1725x1170 physical pixels; applying
        // the DIP numbers directly would give a window two thirds of the intended size.
        var bounds = WindowGeometry.Default(Uhd, UhdScale);

        Assert.Equal(1725, bounds.Width);
        Assert.Equal(1170, bounds.Height);
    }

    [Fact]
    public void The_default_window_is_centred_on_the_work_area()
    {
        var bounds = WindowGeometry.Default(Hd, scale: 1.0);

        Assert.Equal((Hd.Width - bounds.Width) / 2, bounds.X);
        Assert.Equal((Hd.Height - bounds.Height) / 2, bounds.Y);
    }

    [Fact]
    public void The_default_window_is_centred_on_the_display_it_opens_on_not_on_the_origin()
    {
        // A second monitor to the right of the first. Centring on the work area rather than on
        // zero is what keeps the window off the primary display.
        var secondary = new WindowBounds(1920, 0, 1920, 1040);

        var bounds = WindowGeometry.Default(secondary, scale: 1.0);

        Assert.True(bounds.X >= secondary.X);
        Assert.True(bounds.Right <= secondary.Right);
    }

    [Fact]
    public void The_default_window_never_exceeds_the_display_it_opens_on()
    {
        // 1150x780 DIPs at 200% is 2300x1560 physical, which does not fit on a 1080p panel at all.
        var bounds = WindowGeometry.Default(Hd, scale: 2.0);

        Assert.Equal(Hd.Width, bounds.Width);
        Assert.Equal(Hd.Height, bounds.Height);
        Assert.Equal(0, bounds.X);
        Assert.Equal(0, bounds.Y);
    }

    [Fact]
    public void A_placement_that_still_fits_is_restored_exactly()
    {
        var saved = new WindowBounds(300, 120, 1400, 800);

        var restored = WindowGeometry.Restore(saved, [Hd], scale: 1.0);

        Assert.Equal(saved, restored);
    }

    [Fact]
    public void A_sideways_overhang_is_kept()
    {
        // Deliberate: a user who pushed the window off the left edge gets it back where they left
        // it, because the part of the title bar still on screen is enough to take hold of.
        var saved = new WindowBounds(-100, 100, 1000, 700);

        var restored = WindowGeometry.Restore(saved, [Hd], scale: 1.0);

        Assert.Equal(saved, restored);
    }

    [Fact]
    public void A_window_above_the_work_area_is_pulled_down_to_it()
    {
        // The one edge treated differently. A title bar above the work area cannot be grabbed at
        // all, and Windows will not let a window be dragged there — so arriving here means the
        // display changed underneath it.
        var saved = new WindowBounds(200, -60, 1000, 700);

        var restored = WindowGeometry.Restore(saved, [Hd], scale: 1.0);

        Assert.NotNull(restored);
        Assert.Equal(Hd.Y, restored.Value.Y);
        Assert.Equal(200, restored.Value.X);
    }

    [Fact]
    public void A_placement_on_a_display_that_is_gone_is_refused()
    {
        // The laptop was undocked. Restoring this would open the window on coordinates with no
        // pixels behind them, which looks exactly like the app failing to start.
        var saved = new WindowBounds(2600, 400, 1200, 800);

        Assert.Null(WindowGeometry.Restore(saved, [Hd], scale: 1.0));
    }

    [Fact]
    public void A_placement_with_only_a_sliver_on_screen_is_refused()
    {
        // Twenty columns of window on the far right of the display: not enough title bar to take
        // hold of, so it is treated the same as a display that is gone.
        var saved = new WindowBounds(1900, 100, 1200, 800);

        Assert.Null(WindowGeometry.Restore(saved, [Hd], scale: 1.0));
    }

    [Fact]
    public void A_placement_mostly_on_a_second_display_is_restored_there()
    {
        var secondary = new WindowBounds(1920, 0, 1920, 1040);
        var saved = new WindowBounds(2200, 150, 1200, 800);

        var restored = WindowGeometry.Restore(saved, [Hd, secondary], scale: 1.0);

        Assert.Equal(saved, restored);
    }

    [Fact]
    public void A_placement_larger_than_the_display_it_lands_on_is_shrunk_to_fit()
    {
        // The 4K monitor was swapped for a 1080p one, or the resolution was lowered.
        var saved = new WindowBounds(0, 0, 3000, 1800);

        var restored = WindowGeometry.Restore(saved, [Hd], scale: 1.0);

        Assert.NotNull(restored);
        Assert.Equal(Hd.Width, restored.Value.Width);
        Assert.Equal(Hd.Height, restored.Value.Height);
    }

    [Fact]
    public void A_window_that_no_longer_fits_is_pulled_back_onto_the_display()
    {
        // Same window, moved right: shrinking it alone would leave it starting past the left edge
        // of what is left, so the position has to come back too.
        var saved = new WindowBounds(1500, 800, 1200, 800);

        var restored = WindowGeometry.Restore(saved, [Hd], scale: 1.0);

        Assert.NotNull(restored);
        Assert.True(restored.Value.Right <= Hd.Right);
        Assert.True(restored.Value.Bottom <= Hd.Bottom);
    }

    [Fact]
    public void A_placement_smaller_than_the_minimum_is_raised_to_it()
    {
        // Only reachable by hand-editing the file, because the presenter's minimum stops the user
        // dragging one this small. Repaired rather than refused: the position is still good.
        var saved = new WindowBounds(200, 150, 300, 200);

        var restored = WindowGeometry.Restore(saved, [Hd], scale: 1.0);

        Assert.NotNull(restored);
        Assert.Equal(WindowGeometry.MinimumWidth, restored.Value.Width);
        Assert.Equal(WindowGeometry.MinimumHeight, restored.Value.Height);
    }

    [Fact]
    public void The_minimum_is_not_allowed_to_exceed_the_display()
    {
        // 900x600 DIPs at 200% is 1800x1200 physical, taller than a 1080p work area. Clamping up to
        // the minimum before clamping down to the display would hand back a window taller than the
        // screen it is on.
        var saved = new WindowBounds(0, 0, 400, 300);

        var restored = WindowGeometry.Restore(saved, [Hd], scale: 2.0);

        Assert.NotNull(restored);
        Assert.True(restored.Value.Height <= Hd.Height);
        Assert.True(restored.Value.Width <= Hd.Width);
    }

    [Fact]
    public void A_placement_recording_no_size_is_refused()
    {
        // An empty or hand-emptied file. Restoring a zero-sized window would be invisible and
        // unrecoverable without deleting the file.
        Assert.Null(WindowGeometry.Restore(new WindowBounds(100, 100, 0, 0), [Hd], scale: 1.0));
    }

    [Fact]
    public void A_placement_is_refused_when_no_display_is_reported()
    {
        Assert.Null(WindowGeometry.Restore(new WindowBounds(100, 100, 1200, 800), [], scale: 1.0));
    }
}
