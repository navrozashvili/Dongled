namespace Dongled.Abstractions;

/// <summary>
/// One audio source a provider can report on.
/// </summary>
/// <remarks>
/// Value equality spans every member, including <c>DisplayName</c> and <c>Detail</c>, so a
/// descriptor is not a stable dictionary key. The first time a vendor API returns a better display
/// name for the same device, a dictionary keyed by descriptor silently grows a second entry for
/// it. Key by <c>SourceId</c> instead.
/// </remarks>
/// <param name="SourceId">
/// Stable, opaque identifier, for example <c>hyperx:cloud-iii-s-wireless:any</c>. Persisted in
/// configuration and matched against rules. Never displayed to a user.
/// </param>
/// <param name="DisplayName">
/// What a user sees, for example <c>HyperX Cloud III S Wireless</c>. Best effort: providers
/// should fall back through whatever names the vendor API offers and use the identifier only
/// as a last resort. Must never be empty: the host rejects a descriptor whose display name is
/// empty or whitespace and logs the failure against the provider that published it.
/// </param>
/// <param name="Detail">
/// Optional qualifier shown beside the name, for example <c>any headset paired to the dongle</c>.
/// </param>
public sealed record AudioSourceDescriptor(string SourceId, string DisplayName, string? Detail);
