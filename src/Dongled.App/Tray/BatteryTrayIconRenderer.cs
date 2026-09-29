using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Dongled.Abstractions;

namespace Dongled.App.Tray;

/// <summary>
/// Draws a battery reading into a notification-area icon.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>HyperXBatteryHID/Ui/BatteryTrayIconRenderer.cs</c> (a sibling repo this project's
/// author daily-drives), which already solves the hard part: 16x16 is too small to just draw a number
/// in. Four ideas make it work. All four are load-bearing — none should be "simplified" later.
/// </para>
/// <para>
/// 1. <b>Colour-banded background</b> carries the coarse signal: red at 15% or below, amber at 35% or
/// below, green above. A user reads the colour before they read the digits.
/// </para>
/// <para>
/// 2. <b>Auto-fitted font</b>, measured down from 10pt in 0.25pt steps until it fits, rather than
/// guessed from digit count. This is what makes <c>7</c>, <c>47</c> and <c>100</c> all legible in the
/// same box.
/// </para>
/// <para>
/// 3. <b>Outlined text</b> — the numeral is drawn eight times in black at ±0.75px, then once in white
/// on top. This is why the renderer needs no knowledge of the taskbar theme: it is legible on light
/// and dark alike. That is why there is no theme parameter anywhere in this type and nothing to
/// invalidate when the theme changes.
/// </para>
/// <para>
/// 4. <b>Every icon built is cached</b>, keyed by state. At most 101 percentages times a few charge
/// states times a couple of sizes; each is built once and kept for the life of this instance. There is
/// no per-refresh allocation and therefore no handle churn to get wrong.
/// </para>
/// <para>
/// Size is a parameter rather than a fixed 16 pixels: the notification area asks for
/// <c>GetSystemMetrics(SM_CXSMICON)</c>, which is 32 at 200% scaling, and letting Windows upscale a
/// 16-pixel icon is visibly soft, so every literal coordinate below is scaled by <c>size / 16f</c>
/// and the cache key includes the size. Charging is described by <see cref="ChargeState"/>: the
/// bolt is drawn for
/// <see cref="ChargeState.Charging"/> and <see cref="ChargeState.Full"/>, the band colour is forced
/// green for <see cref="ChargeState.Full"/> regardless of percentage because a docked, full device is
/// not low on anything, and <see cref="ChargeState.Unknown"/> draws no bolt.
/// </para>
/// <para>
/// One targeted fix was layered on afterwards, at 100 specifically: see the "on power" comment in
/// <see cref="CreateIcon"/> for why three digits and a bolt cannot share this box, and why the
/// charging signal moves to the background colour there instead of being dropped.
/// </para>
/// </remarks>
internal sealed partial class BatteryTrayIconRenderer : ITrayIconRenderer
{
    private readonly object _cacheLock = new();
    private readonly Dictionary<(int PercentKey, ChargeState Charge, int Size), Icon> _cache = [];
    private readonly Icon _baseIcon;
    private bool _disposed;

    public BatteryTrayIconRenderer(string baseIconPath)
    {
        ArgumentNullException.ThrowIfNull(baseIconPath);

        if (!File.Exists(baseIconPath))
        {
            // Better here than at the first render, which happens on the UI thread during startup.
            throw new FileNotFoundException("The tray base icon was not found.", baseIconPath);
        }

        _baseIcon = new Icon(baseIconPath);
    }

    /// <inheritdoc />
    public Icon Render(TrayIconContent content, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        var percent = content.Percent;
        var percentKey = percent is >= 0 and <= 100 ? percent.Value : -1;

        // Render is called from the UI thread today, but it sits behind an interface and the next
        // caller may not be the UI thread, so the cache — and the disposed check — stay protected
        // regardless. The check has to be in here, ahead of the no-level short-circuit below: that
        // path returns _baseIcon directly, and without the lock a Dispose() on another thread could
        // destroy that same handle between the check and the return.
        lock (_cacheLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (percentKey == -1)
            {
                // This app's tray icon is also its identity: a grey "--" plate where the glyph
                // belongs would read as broken, so the fallback is the real icon rather than a
                // drawn placeholder.
                return _baseIcon;
            }

            var key = (percentKey, content.Charge, size);

            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var created = CreateIcon(percentKey, content.Charge, size);
            _cache[key] = created;
            return created;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Same lock as Render, and the disposed flag/cache teardown/base-icon disposal all happen
        // inside it, for the same reason Render's check does: the next caller may not be the UI
        // thread, so a shutdown on one thread must not be able to interleave with a refresh on
        // another and hand out (or leak) a handle this call is in the middle of destroying.
        lock (_cacheLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (var icon in _cache.Values)
            {
                icon.Dispose();
            }

            _cache.Clear();
            _baseIcon.Dispose();
        }
    }

    private static Icon CreateIcon(int percent, ChargeState charge, int size)
    {
        var scale = size / 16f;

        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;

        // The bolt marks both actively charging and topped-off-on-the-dock; Unknown draws neither.
        var wouldDrawBolt = charge is ChargeState.Charging or ChargeState.Full;

        // 100 is the only three-digit reading in [0, 100], and three digits plus a bolt cannot share
        // this 16-pixel box: the font is FITTED against the narrower charging `fitRect` (11 of the 16
        // pixels wide, to leave room for the bolt) but is still DRAWN centred in the full-width
        // `textRect` below, so the digits span the full box regardless and the bolt collides with the
        // last one. Shrinking the font further would make 100 visibly smaller than 99; dropping the
        // charging signal outright would lose information. Instead, at 100 only, the signal moves from
        // the bolt to a distinct "on power" background colour — consistent with idea 1 above: colour
        // carries the coarse signal and is read before the digits. Below 100 this branch never taken;
        // the bolt and the percentage bands are exactly as before.
        var isFullOnPower = wouldDrawBolt && percent == 100;
        var drawBolt = wouldDrawBolt && !isFullOnPower;

        // Background colour conveys status; text stays readable in both light/dark taskbars.
        // Full is forced green regardless of percentage: a docked, full device is not low on anything.
        // The on-power blue shares the other bands' 235 alpha deliberately: a sliver of taskbar
        // shows through so the plate reads as a status colour rather than a hard sticker.
        var bg = isFullOnPower ? Color.FromArgb(235, 30, 111, 191)
            : charge == ChargeState.Full ? Color.FromArgb(235, 20, 140, 60)
            : percent <= 15 ? Color.FromArgb(235, 170, 35, 35)
            : percent <= 35 ? Color.FromArgb(235, 190, 140, 20)
            : Color.FromArgb(235, 20, 140, 60);

        var bgRect = new Rectangle(0, 0, size, size);
        using (var bgBrush = new SolidBrush(bg))
        {
            g.FillRectangle(bgBrush, bgRect);
        }

        using (var borderPen = new Pen(Color.FromArgb(200, 0, 0, 0), 1))
        {
            g.DrawRectangle(borderPen, 0, 0, size - 1, size - 1);
        }

        var text = percent.ToString(CultureInfo.InvariantCulture);

        // Auto-fit text into the available box (size x size minus a small margin).
        // This is more reliable than guessing sizes by digit count across Windows font metrics.
        // When charging (or full), reserve top-right space for the bolt so it doesn't collide with
        // the digits. Coordinates are expressed against a 16-pixel box, scaled by
        // `scale` so the same layout holds at every requested size.
        var boltRect = drawBolt
            ? new RectangleF(11.0f * scale, 0.5f * scale, 4.5f * scale, 7.5f * scale)
            : RectangleF.Empty;
        var textRect = drawBolt
            ? new RectangleF(-1.0f * scale, 0, 16f * scale, 16f * scale)
            : new RectangleF(0, 0, 16f * scale, 16f * scale);
        var fitRect = drawBolt
            ? new RectangleF(0.5f * scale, 0.5f * scale, 11.0f * scale, 15.0f * scale)
            : new RectangleF(0.5f * scale, 0.5f * scale, 15.0f * scale, 15.0f * scale);
        using var font = CreateFittedFont(
            g, text, "Segoe UI", FontStyle.Bold, fitRect, maxPt: 10.0f * scale, minPt: 5.5f * scale);

        // Draw outlined text (stroke simulated by offsets) for maximum contrast. This, not a
        // theme-aware colour, is what keeps the digits legible on a light or dark taskbar without the
        // renderer knowing which.
        DrawCenteredOutlined(g, text, font, fill: Color.White, outline: Color.Black, textRect, 0.75f * scale);

        if (drawBolt)
        {
            // Small lightning bolt overlay in the top-right (inside reserved area).
            DrawChargingBolt(g, boltRect);
        }

        var hIcon = bmp.GetHicon();
        try
        {
            // Clone to detach from the HICON handle we will destroy.
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static void DrawCentered(Graphics g, string text, Font font, Brush brush, RectangleF rect, float dx, float dy)
    {
        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            // Left at the default (Character), GDI+ silently drops a trailing glyph whenever the
            // fitted font's actual draw width (measured with side bearings) exceeds the box by even a
            // fraction of a pixel — which the typographic measurement CreateFittedFont fits against
            // does not foresee, because it excludes those bearings. At 16px the two measurements
            // never diverged enough to trigger it; at 32px they did, and a whole digit vanished
            // instead of the sub-pixel overflow being clipped. None restores plain clipping.
            Trimming = StringTrimming.None,
        };

        var r = rect;
        r.Offset(dx, dy);
        g.DrawString(text, font, brush, r, sf);
    }

    private static void DrawCenteredOutlined(
        Graphics g, string text, Font font, Color fill, Color outline, RectangleF rect, float outlineOffset)
    {
        using var fillBrush = new SolidBrush(fill);
        using var outlineBrush = new SolidBrush(outline);

        // Outline (8 directions) then fill.
        var o = outlineOffset;
        DrawCentered(g, text, font, outlineBrush, rect, -o, 0f);
        DrawCentered(g, text, font, outlineBrush, rect, o, 0f);
        DrawCentered(g, text, font, outlineBrush, rect, 0f, -o);
        DrawCentered(g, text, font, outlineBrush, rect, 0f, o);
        DrawCentered(g, text, font, outlineBrush, rect, -o, -o);
        DrawCentered(g, text, font, outlineBrush, rect, o, -o);
        DrawCentered(g, text, font, outlineBrush, rect, -o, o);
        DrawCentered(g, text, font, outlineBrush, rect, o, o);
        DrawCentered(g, text, font, fillBrush, rect, 0f, 0f);
    }

    private static Font CreateFittedFont(
        Graphics g, string text, string family, FontStyle style, RectangleF fitRect, float maxPt, float minPt)
    {
        // Measure using typographic settings so we don't over-estimate widths.
        using var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
        sf.FormatFlags |= StringFormatFlags.NoWrap;
        sf.Trimming = StringTrimming.None;

        // Leave a small safety margin for outline and pixel rounding.
        var maxW = Math.Max(1f, fitRect.Width - 1.5f);
        var maxH = Math.Max(1f, fitRect.Height - 1.0f);

        for (var pt = maxPt; pt >= minPt; pt -= 0.25f)
        {
            var f = new Font(family, pt, style, GraphicsUnit.Point);
            var measured = g.MeasureString(text, f, new SizeF(100, 100), sf);
            if (measured.Width <= maxW && measured.Height <= maxH)
            {
                return f;
            }

            f.Dispose();
        }

        return new Font(family, minPt, style, GraphicsUnit.Point);
    }

    private static void DrawChargingBolt(Graphics g, RectangleF rect)
    {
        // Simple bolt polygon sized to fit within 'rect', drawn with a dark outline for contrast.
        // Coordinates are expressed in 0..1 and then scaled into the rectangle.
        static PointF P(RectangleF r, float x, float y) => new(r.Left + (r.Width * x), r.Top + (r.Height * y));

        var pts = new[]
        {
            P(rect, 0.65f, 0.00f),
            P(rect, 0.15f, 0.55f),
            P(rect, 0.50f, 0.55f),
            P(rect, 0.30f, 1.00f),
            P(rect, 1.00f, 0.50f),
            P(rect, 0.65f, 0.50f),
        };

        using var fill = new SolidBrush(Color.FromArgb(245, 255, 220, 50));
        using var outline = new Pen(Color.FromArgb(220, 0, 0, 0), 1);
        g.FillPolygon(fill, pts);
        g.DrawPolygon(outline, pts);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);
}
