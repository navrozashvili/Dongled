using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Dongled.Core.Updates;

/// <summary>Reads the version strings a build and a release tag carry, and compares them numerically.</summary>
/// <remarks>
/// <para>
/// Accepts <c>1.0.57</c>, <c>v1.0.57</c>, <c>1.0.57+abc123</c> and <c>0.0.0-dev</c>. The build
/// metadata after <c>+</c> is dropped because it names a commit, not an order. A pre-release
/// label after <c>-</c> is dropped too: releases are never published as pre-releases, so the only
/// place one appears is a development build, and those never check.
/// </para>
/// <para>
/// The result always has all four components, missing ones as zero, because
/// <see cref="Version"/> ranks <c>1.0</c> below <c>1.0.0</c> otherwise.
/// </para>
/// </remarks>
public static class ReleaseVersion
{
    /// <summary>Parse <paramref name="text"/>, or return false if it holds no usable version.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out Version? version)
    {
        version = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().Trim();

        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
        {
            span = span[1..];
        }

        var plus = span.IndexOf('+');
        if (plus >= 0)
        {
            span = span[..plus];
        }

        var dash = span.IndexOf('-');
        if (dash >= 0)
        {
            span = span[..dash];
        }

        // Digits and dots only. Version.TryParse alone would accept a leading sign or white space
        // inside a component.
        foreach (var c in span)
        {
            if (c != '.' && !char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        if (!Version.TryParse(span, out var parsed) || parsed.Major < 0)
        {
            return false;
        }

        version = new Version(
            parsed.Major,
            parsed.Minor,
            Math.Max(parsed.Build, 0),
            Math.Max(parsed.Revision, 0));

        return true;
    }

    /// <summary>The form shown to a user and used in file names: three components, or four when the fourth is not zero.</summary>
    public static string Format(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return version.Revision > 0
            ? version.ToString(4)
            : string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}");
    }
}
