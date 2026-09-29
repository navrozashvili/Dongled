namespace Dongled.Abstractions;

/// <summary>
/// Minimal logging surface handed to a provider. The host adapts this onto its own logger.
/// </summary>
/// <remarks>
/// The host supplies the implementation. Both members are safe to call from any thread, so a
/// provider may log directly from a vendor SDK callback without marshalling first.
/// </remarks>
public interface IProviderLogger
{
    /// <summary>
    /// Whether a message at this level would be recorded. Check before building an expensive
    /// message; the host filters per plugin and the default is <see cref="ProviderLogLevel.Warning"/>.
    /// Returns <see langword="false"/> for <see cref="ProviderLogLevel.None"/>, matching
    /// <c>Microsoft.Extensions.Logging</c> semantics.
    /// </summary>
    bool IsEnabled(ProviderLogLevel level);

    /// <summary>Record a message, optionally with the exception that caused it.</summary>
    void Log(ProviderLogLevel level, string message, Exception? exception = null);
}
