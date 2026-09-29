using System.Drawing;

namespace Dongled.App.Tray;

/// <summary>
/// Fallback used in place of <see cref="BatteryTrayIconRenderer"/> when its base icon file could not
/// be loaded — most likely a missing or unreadable <c>Assets/TrayIcon.ico</c> from a partial deploy.
/// </summary>
/// <remarks>
/// Mirrors <c>MainWindow.SetWindowIcon</c>'s policy for the same file: an icon is decoration, and its
/// absence must not stop the window opening. There is no base icon left to draw a battery figure
/// onto, so this always hands back the system's generic application icon rather than attempting to
/// draw anything.
/// </remarks>
internal sealed class NullTrayIconRenderer : ITrayIconRenderer
{
    /// <inheritdoc />
    public Icon Render(TrayIconContent content, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        return SystemIcons.Application;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // SystemIcons.Application is a handle owned by the framework, not by this instance, so
        // there is nothing here to release.
    }
}
