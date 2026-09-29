namespace Dongled.Abstractions;

/// <summary>
/// Identity of a provider, shown on the Plugins page.
/// </summary>
/// <param name="Id">Stable identifier, for example <c>hyperx.cloud3s.hid</c>.</param>
/// <param name="DisplayName">Human-readable name, for example <c>HyperX Cloud III S Wireless (HID)</c>.</param>
/// <param name="Description">Optional one-line explanation of what the provider watches.</param>
/// <param name="IsExperimental">
/// True if the provider is known to be fragile, for example because it drives undocumented
/// vendor software. The UI badges these.
/// </param>
public sealed record ProviderMetadata(string Id, string DisplayName, string? Description, bool IsExperimental);
