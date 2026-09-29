using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.UI;

namespace Dongled.App.Windowing;

/// <summary>Colours the window's caption buttons to match the app's theme.</summary>
/// <remarks>
/// The caption buttons are drawn by Windows rather than XAML, so they do not follow the app's theme
/// on their own.
/// </remarks>
internal static class CaptionButtons
{
    /// <summary>Tint the caption buttons for a light or dark theme.</summary>
    /// <remarks>
    /// Backgrounds are transparent so the window's material shows through and the caption reads as
    /// part of the window. Only the glyph colour and the hover and pressed tints change.
    /// </remarks>
    public static void Apply(AppWindowTitleBar bar, bool dark)
    {
        ArgumentNullException.ThrowIfNull(bar);

        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;

        bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonPressedForegroundColor = dark ? Colors.White : Colors.Black;

        bar.ButtonInactiveForegroundColor = dark
            ? Color.FromArgb(255, 150, 150, 150)
            : Color.FromArgb(255, 110, 110, 110);

        bar.ButtonHoverBackgroundColor = dark
            ? Color.FromArgb(40, 255, 255, 255)
            : Color.FromArgb(25, 0, 0, 0);

        bar.ButtonPressedBackgroundColor = dark
            ? Color.FromArgb(60, 255, 255, 255)
            : Color.FromArgb(40, 0, 0, 0);
    }
}
