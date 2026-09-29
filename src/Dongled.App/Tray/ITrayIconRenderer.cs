using System.Drawing;

namespace Dongled.App.Tray;

/// <summary>
/// Turns a battery reading into the icon the notification area shows.
/// </summary>
/// <remarks>
/// <para>
/// A seam rather than a method on the window for two reasons. The drawing is the one part of this
/// feature with no meaningful test coverage — an icon either looks right or it does not — so keeping
/// it behind an interface lets everything around it be tested without drawing anything. And it is
/// the one part that does not survive leaving Windows: a future port swaps the implementation
/// instead of unpicking the shell.
/// </para>
/// <para>
/// The renderer owns every icon it returns and keeps them for its own lifetime. A caller must never
/// dispose one.
/// </para>
/// </remarks>
internal interface ITrayIconRenderer : IDisposable
{
    /// <summary>
    /// Draw the icon for this content. Never returns null: with no level to show it returns the
    /// plain application icon, so the caller has one code path rather than a decision.
    /// </summary>
    /// <param name="content">What to show.</param>
    /// <param name="size">
    /// Square edge in pixels, normally <c>GetSystemMetrics(SM_CXSMICON)</c>. Passed in rather than
    /// assumed to be 16, because that is 32 at 200% scaling and letting Windows upscale a 16-pixel
    /// icon is visibly soft. Must be positive. Ignored for the no-level fallback: the base icon
    /// file holds a single 32x32 frame, and .NET's <c>Icon(path, width, height)</c> constructor
    /// picks the closest-matching frame rather than resampling it, so that path is always returned
    /// at its native size and the shell scales it if needed. (A consequence, not a bug: because
    /// that path returns before the cache is consulted, an implementation's cache can never hold an
    /// entry keyed on "no level" — a reader staring at the cache key later should not go looking
    /// for one.)
    /// </param>
    /// <returns>An icon owned by this renderer, valid until it is disposed.</returns>
    Icon Render(TrayIconContent content, int size);
}
