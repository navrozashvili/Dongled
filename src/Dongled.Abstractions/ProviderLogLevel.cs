namespace Dongled.Abstractions;

/// <summary>
/// Severity of a provider log line.
/// </summary>
/// <remarks>
/// Members and numeric values match <c>Microsoft.Extensions.Logging.LogLevel</c> exactly, so the
/// host can map between them with a cast. This enum exists at all because Abstractions is
/// dependency-free by design, so a plugin is never coupled to a logging package version.
/// </remarks>
public enum ProviderLogLevel
{
    /// <summary>Extremely verbose; may contain sensitive detail.</summary>
    Trace = 0,

    /// <summary>Diagnostic detail useful while developing a provider.</summary>
    Debug = 1,

    /// <summary>Normal operational milestones.</summary>
    Information = 2,

    /// <summary>Something unexpected that the provider recovered from. The default level.</summary>
    Warning = 3,

    /// <summary>The provider could not complete an operation.</summary>
    Error = 4,

    /// <summary>The provider cannot continue.</summary>
    Critical = 5,

    /// <summary>Logging disabled. Never passed to <see cref="IProviderLogger.Log"/>.</summary>
    None = 6,
}
