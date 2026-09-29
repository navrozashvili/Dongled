using System.Reflection;

namespace Dongled.Core.Updates;

/// <summary>How the running build was published, which decides which release asset replaces it.</summary>
public enum BuildFlavor
{
    /// <summary>The build does not say. Nothing can be downloaded for it.</summary>
    Unknown = 0,

    /// <summary>Carries its own .NET runtime.</summary>
    SelfContained = 1,

    /// <summary>Uses the .NET runtime installed on the machine.</summary>
    FrameworkDependent = 2,
}

/// <summary>What the running build says about itself: whether it is a published release, how it was published, and its version.</summary>
/// <param name="IsOfficial">
/// Whether this is a build the release pipeline published. Only such a build ever checks for
/// updates; a build from source never makes a request.
/// </param>
/// <param name="Flavor">How it was published.</param>
/// <param name="Version">The numeric version, or null if the build carries none that parses.</param>
/// <param name="DisplayVersion">The version as a user should read it, without any commit suffix.</param>
public sealed record BuildInfo(bool IsOfficial, BuildFlavor Flavor, Version? Version, string DisplayVersion)
{
    /// <summary>Assembly metadata key the release pipeline sets to <c>true</c> on a published build.</summary>
    public const string OfficialBuildKey = "DongledOfficialBuild";

    /// <summary>Assembly metadata key naming the publish flavor.</summary>
    public const string FlavorKey = "DongledFlavor";

    /// <summary>A build that never checks: what a build from source is.</summary>
    public static BuildInfo Development { get; } = new(false, BuildFlavor.Unknown, null, "development build");

    /// <summary>
    /// Whether updates can be checked for and installed at all. Requires an official build that
    /// knows both its version and its flavor, so a half-stamped build fails closed.
    /// </summary>
    public bool CanUpdate => IsOfficial && Version is not null && Flavor != BuildFlavor.Unknown;

    /// <summary>Read the metadata the release pipeline stamps onto <paramref name="assembly"/>.</summary>
    /// <remarks>
    /// Anything missing or unexpected reads as a development build. Only the exact value
    /// <c>true</c> makes a build official, so a typo in the pipeline turns update checks off rather
    /// than on.
    /// </remarks>
    public static BuildInfo FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        string? official = null;
        string? flavor = null;

        foreach (var metadata in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(metadata.Key, OfficialBuildKey, StringComparison.Ordinal))
            {
                official = metadata.Value;
            }
            else if (string.Equals(metadata.Key, FlavorKey, StringComparison.Ordinal))
            {
                flavor = metadata.Value;
            }
        }

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return Create(official, flavor, informational ?? assembly.GetName().Version?.ToString());
    }

    /// <summary>Interpret raw metadata values, as <see cref="FromAssembly"/> reads them.</summary>
    public static BuildInfo Create(string? officialBuild, string? flavor, string? informationalVersion)
    {
        var isOfficial = string.Equals(officialBuild, "true", StringComparison.Ordinal);

        var parsedFlavor = flavor switch
        {
            "self-contained" => BuildFlavor.SelfContained,
            "framework-dependent" => BuildFlavor.FrameworkDependent,
            _ => BuildFlavor.Unknown,
        };

        var display = StripCommit(informationalVersion);
        var version = ReleaseVersion.TryParse(display, out var parsed) ? parsed : null;

        return new BuildInfo(isOfficial, parsedFlavor, version, display.Length > 0 ? display : "unknown");
    }

    private static string StripCommit(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return string.Empty;
        }

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return (plus < 0 ? informationalVersion : informationalVersion[..plus]).Trim();
    }
}
