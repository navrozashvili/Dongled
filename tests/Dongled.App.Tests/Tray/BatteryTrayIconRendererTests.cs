using System.Drawing;
using Dongled.Abstractions;
using Dongled.App.Tray;
using Xunit;

namespace Dongled.App.Tests.Tray;

public class BatteryTrayIconRendererTests
{
    private static string BaseIcon => Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIcon.ico");

    [Fact]
    public void The_base_icon_ships_beside_the_test_host()
    {
        // If this fails the Content copy rule changed and every other test here exercises the
        // fallback path instead of the real one.
        Assert.True(File.Exists(BaseIcon));
    }

    [Fact]
    public void A_reading_produces_an_icon()
    {
        using var renderer = new BatteryTrayIconRenderer(BaseIcon);

        Assert.NotNull(renderer.Render(new TrayIconContent(87, ChargeState.Discharging), 16));
    }

    [Fact]
    public void No_reading_produces_the_plain_application_icon()
    {
        // One code path, so the caller never has to decide whether to clear.
        using var renderer = new BatteryTrayIconRenderer(BaseIcon);

        Assert.NotNull(renderer.Render(new TrayIconContent(null, ChargeState.Unknown), 16));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(47)]
    [InlineData(100)]
    public void Every_digit_count_renders(int percent)
    {
        // The auto-fitted font is what makes one, two and three digits all work at 16 pixels.
        using var renderer = new BatteryTrayIconRenderer(BaseIcon);

        Assert.NotNull(renderer.Render(new TrayIconContent(percent, ChargeState.Discharging), 16));
    }

    [Theory]
    [InlineData(ChargeState.Unknown)]
    [InlineData(ChargeState.Discharging)]
    [InlineData(ChargeState.Charging)]
    [InlineData(ChargeState.Full)]
    public void Every_charge_state_renders(ChargeState charge)
    {
        using var renderer = new BatteryTrayIconRenderer(BaseIcon);

        Assert.NotNull(renderer.Render(new TrayIconContent(50, charge), 16));
    }

    [Fact]
    public void The_same_state_returns_the_same_cached_instance()
    {
        // The cache is what removes per-refresh allocation, and with it any handle churn.
        using var renderer = new BatteryTrayIconRenderer(BaseIcon);

        var first = renderer.Render(new TrayIconContent(87, ChargeState.Discharging), 16);
        var second = renderer.Render(new TrayIconContent(87, ChargeState.Discharging), 16);

        Assert.Same(first, second);
    }

    [Fact]
    public void A_different_size_is_a_different_icon()
    {
        using var renderer = new BatteryTrayIconRenderer(BaseIcon);

        var small = renderer.Render(new TrayIconContent(87, ChargeState.Discharging), 16);
        var large = renderer.Render(new TrayIconContent(87, ChargeState.Discharging), 32);

        Assert.NotSame(small, large);
    }

    [Fact]
    public void A_different_percent_is_a_different_icon()
    {
        // The cache key is (PercentKey, Charge, Size). Charge and Size are each pinned by the two
        // tests above; without this one, a key that dropped PercentKey (returning one icon for all
        // 101 levels) would pass the rest of the suite undetected, because every other test either
        // only checks NotNull or builds a fresh renderer per case instead of reusing one instance
        // across two different percentages.
        using var renderer = new BatteryTrayIconRenderer(BaseIcon);

        var first = renderer.Render(new TrayIconContent(87, ChargeState.Discharging), 16);
        var second = renderer.Render(new TrayIconContent(12, ChargeState.Discharging), 16);

        Assert.NotSame(first, second);
    }

    [Fact]
    public void Every_level_renders()
    {
        using var renderer = new BatteryTrayIconRenderer(BaseIcon);

        for (var pass = 0; pass < 2; pass++)
        {
            for (var percent = 0; percent <= 100; percent++)
            {
                Assert.NotNull(renderer.Render(new TrayIconContent(percent, ChargeState.Discharging), 16));
            }
        }
    }

    [Fact]
    public void The_three_colour_bands_paint_three_different_backgrounds()
    {
        // Idea 1 of the four the renderer calls load-bearing: red at 15% or below, amber at 35% or
        // below, green above, because a user reads the colour before they read the digits. Nothing
        // else in this file samples a background at more than one percentage, so transposing the two
        // thresholds - or collapsing all three bands into one colour - is invisible to every other
        // test here.
        using var renderer = new BatteryTrayIconRenderer(BaseIcon);

        var low = Background(renderer, 10);
        var middle = Background(renderer, 25);
        var high = Background(renderer, 60);

        Assert.NotEqual(low, middle);
        Assert.NotEqual(middle, high);
        Assert.NotEqual(low, high);

        // Three different colours is not yet three meaningful ones: this is what stops the bands
        // being swapped end for end, which the pairwise comparison alone would not notice.
        Assert.True(low.R > low.G, $"the low band should read as a warning, but was {low}");
        Assert.True(high.G > high.R, $"the high band should read as healthy, but was {high}");
    }

    /// <summary>
    /// The background colour an icon is painted in, sampled where nothing else can be drawn.
    /// </summary>
    /// <remarks>
    /// The same pixel
    /// <see cref="At_100_percent_charging_or_full_paints_a_different_background_than_discharging"/>
    /// established as reliable background: a couple of pixels in from the bottom-left corner, which
    /// is farthest from both the centred digits and the top-right bolt slot. Discharging, so no bolt
    /// is drawn and no charge state can force a band; 32 pixels, matching the other sampling test.
    /// </remarks>
    private static Color Background(BatteryTrayIconRenderer renderer, int percent)
    {
        using var bitmap = renderer.Render(new TrayIconContent(percent, ChargeState.Discharging), 32).ToBitmap();

        return bitmap.GetPixel(2, bitmap.Height - 3);
    }

    [Theory]
    [InlineData(87)]
    [InlineData(null)]
    public void Disposing_a_clone_leaves_the_renderers_own_icon_intact(int? percent)
    {
        // What TrayIconBinding.ForControl rests on. H.NotifyIcon's TaskbarIcon.Icon disposes the value
        // it displaces, while this renderer owns everything it returns and serves the same instance
        // again whenever a state recurs — so the control is handed a clone instead of the original.
        // That is only sound if Icon.Clone duplicates the underlying HICON rather than sharing it,
        // which is a System.Drawing behaviour nothing in this repo controls and nothing else here
        // pins: if it shared, the clone the control destroys would take the cached original's handle
        // with it and every later render of that state would draw from a dead handle.
        //
        // Both arguments matter. 87 is an icon the renderer drew and cached; null is the base icon,
        // which is returned ahead of the cache and is also what the shell's failure path falls back
        // to, so it is the instance most often displaced.
        using var renderer = new BatteryTrayIconRenderer(BaseIcon);

        var content = new TrayIconContent(percent, ChargeState.Discharging);
        var original = renderer.Render(content, 16);
        var handle = original.Handle;

        var clone = (Icon)original.Clone();
        Assert.NotEqual(handle, clone.Handle);
        clone.Dispose();

        // Still the same instance, still the same live handle, and still able to produce pixels —
        // the last of which is the part that would fail if the handle had been destroyed underneath
        // it while the managed object carried on looking healthy.
        Assert.Same(original, renderer.Render(content, 16));
        Assert.Equal(handle, original.Handle);

        using var bitmap = original.ToBitmap();

        Assert.True(bitmap.Width > 0);
    }

    [Fact]
    public void A_missing_base_icon_is_refused_at_construction()
    {
        // Better here than at the first render, which happens on the UI thread during startup.
        Assert.Throws<FileNotFoundException>(
            () => new BatteryTrayIconRenderer(Path.Combine(AppContext.BaseDirectory, "nope.ico")));
    }

    [Theory]
    [InlineData(ChargeState.Charging)]
    [InlineData(ChargeState.Full)]
    public void At_100_percent_charging_or_full_paints_a_different_background_than_discharging(ChargeState charge)
    {
        // Three digits plus a bolt cannot share this box (see BatteryTrayIconRenderer's remarks), so
        // at 100 the charging signal moves from the bolt to the background colour instead. This pins
        // that: if the rule were removed, 100 + Charging/Full would fall back to sharing Discharging's
        // green (the bolt was the only difference before), so the sampled pixel would match instead of
        // differing.
        using var renderer = new BatteryTrayIconRenderer(BaseIcon);

        var onPower = renderer.Render(new TrayIconContent(100, charge), 32);
        var discharging = renderer.Render(new TrayIconContent(100, ChargeState.Discharging), 32);

        using var onPowerBitmap = onPower.ToBitmap();
        using var dischargingBitmap = discharging.ToBitmap();

        // A couple of pixels in from the bottom-left corner: farthest from both the centred digits
        // and the top-right bolt slot, so it is background in every percent/charge combination.
        var onPowerPixel = onPowerBitmap.GetPixel(2, onPowerBitmap.Height - 3);
        var dischargingPixel = dischargingBitmap.GetPixel(2, dischargingBitmap.Height - 3);

        Assert.NotEqual(dischargingPixel, onPowerPixel);

        // The bottom-left sample above only pins the background colour swap; it says nothing about
        // whether the bolt itself was actually removed. A version that kept drawing the bolt on top
        // of the new blue background would still pass it (three digits colliding with a re-added
        // bolt was the exact defect this branch exists to prevent). This second sample sits inside
        // DrawChargingBolt's fill at this icon's 32px size (verified empirically against a bolt
        // actually being drawn, e.g. at (63, Charging): the bolt's yellow fill covers roughly
        // x in [25,29], y in [5,12]; (26, 8) sits solidly inside that, not merely inside the
        // reserved bounding box, which matters because the bolt polygon is concave). At 100 it must
        // read as plain background — i.e. match the bottom-left sample exactly — because no bolt is
        // drawn there at all.
        var onPowerBoltRegionPixel = onPowerBitmap.GetPixel(26, 8);

        Assert.Equal(onPowerPixel, onPowerBoltRegionPixel);
    }

    [Theory]
    [InlineData(ChargeState.Charging)]
    [InlineData(ChargeState.Full)]
    public void The_same_100_percent_charging_state_still_returns_the_same_cached_instance(ChargeState charge)
    {
        // The 100-percent special case adds a branch ahead of the existing background/bolt logic;
        // this confirms it did not accidentally bypass the cache lookup in Render.
        using var renderer = new BatteryTrayIconRenderer(BaseIcon);

        var first = renderer.Render(new TrayIconContent(100, charge), 32);
        var second = renderer.Render(new TrayIconContent(100, charge), 32);

        Assert.Same(first, second);
    }

    [Fact]
    public void Render_after_dispose_throws_for_the_no_level_path()
    {
        // Without the disposed check, this path returns _baseIcon, which Dispose already disposed —
        // the caller gets back an Icon whose handle is already destroyed instead of a clear error.
        var renderer = new BatteryTrayIconRenderer(BaseIcon);
        renderer.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => renderer.Render(new TrayIconContent(null, ChargeState.Unknown), 16));
    }

    [Fact]
    public void Render_after_dispose_throws_for_a_normal_level()
    {
        // Without the disposed check, this path re-enters the (already-cleared) cache, builds a
        // fresh icon, and stores it where nothing will ever dispose it again — a leaked HICON per
        // post-dispose call.
        var renderer = new BatteryTrayIconRenderer(BaseIcon);
        renderer.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => renderer.Render(new TrayIconContent(87, ChargeState.Discharging), 16));
    }
}
