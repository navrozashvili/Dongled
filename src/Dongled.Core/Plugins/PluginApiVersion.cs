using System.Globalization;
using Dongled.Abstractions;

namespace Dongled.Core.Plugins;

/// <summary>
/// The plugin API version this host provides, and the rule for whether a plugin built against some
/// other version may run.
/// </summary>
/// <remarks>
/// Compatibility needs no author action and cannot be misdeclared, because the host reads
/// the plugin assembly's own metadata reference to <c>Dongled.Abstractions</c> rather
/// than any value the plugin states about itself. There is nothing to declare and nothing to lie
/// about.
/// </remarks>
internal static class PluginApiVersion
{
    /// <summary>
    /// The version of <c>Dongled.Abstractions</c> loaded in this process, which is the
    /// one a plugin will actually bind against.
    /// </summary>
    /// <remarks>
    /// Read from the assembly, not repeated as a constant, so the csproj's <c>Version</c> element
    /// stays the single source of truth its comment claims to be.
    /// </remarks>
    public static Version Host { get; } =
        typeof(IAudioSourceProvider).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>
    /// Whether a plugin built against <paramref name="plugin"/> may run on a host providing
    /// <paramref name="host"/>.
    /// </summary>
    /// <remarks>
    /// The SDK's versioning promise is minor for additive changes, major for breaking ones. So a
    /// plugin may be built against an older or equal minor of the same major: every member it
    /// compiled against still exists. It may not be built against a newer minor, because it may
    /// reference a member this host has never heard of, and a missing member surfaces as a
    /// type-load error at some arbitrary later moment rather than as a refusal now. Build and
    /// revision are ignored; the SDK generates them.
    /// </remarks>
    public static bool IsCompatible(Version plugin, Version host)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(host);

        return plugin.Major == host.Major && plugin.Minor <= host.Minor;
    }

    /// <summary>Major and minor only, for a message a user reads.</summary>
    public static string Describe(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}");
    }
}
