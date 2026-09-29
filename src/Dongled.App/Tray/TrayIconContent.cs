using Dongled.Abstractions;

namespace Dongled.App.Tray;

/// <summary>What the notification-area icon has to say right now.</summary>
/// <remarks>
/// Deliberately not a source or a view model. The renderer is handed a level and a charge state and
/// nothing else, so it has no opinion about which device they came from and swapping it for a
/// different drawing strategy cannot change which device was chosen.
/// </remarks>
/// <param name="Percent">The level to draw, or null to draw the plain application icon.</param>
/// <param name="Charge">Whether to mark it as charging.</param>
internal readonly record struct TrayIconContent(int? Percent, ChargeState Charge);
