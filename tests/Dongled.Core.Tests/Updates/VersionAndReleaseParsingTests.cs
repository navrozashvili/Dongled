using System.Reflection;
using Dongled.Core.Updates;
using Xunit;

namespace Dongled.Core.Tests.Updates;

public sealed class VersionAndReleaseParsingTests
{
    [Theory]
    [InlineData("1.0.57", "1.0.57.0")]
    [InlineData("v1.0.57", "1.0.57.0")]
    [InlineData("1.0.57+3f2a1bc", "1.0.57.0")]
    [InlineData("0.0.0-dev", "0.0.0.0")]
    [InlineData("1.2", "1.2.0.0")]
    public void Versions_parse_without_their_suffixes(string text, string expected)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.0.x")]
    [InlineData("-1.0.0")]
    public void Nonsense_is_not_a_version(string? text) =>
        Assert.False(ReleaseVersion.TryParse(text, out _));

    [Fact]
    public void Versions_compare_numerically_not_as_text()
    {
        Assert.True(ReleaseVersion.TryParse("1.0.10", out var ten));
        Assert.True(ReleaseVersion.TryParse("1.0.9", out var nine));
        Assert.True(ten > nine);
    }

    [Fact]
    public void Only_the_exact_value_true_makes_a_build_official()
    {
        Assert.True(BuildInfo.Create("true", "self-contained", "1.0.57").CanUpdate);
        Assert.False(BuildInfo.Create("True", "self-contained", "1.0.57").IsOfficial);
        Assert.False(BuildInfo.Create("false", "self-contained", "1.0.57").CanUpdate);
        Assert.False(BuildInfo.Create(null, null, null).CanUpdate);
    }

    [Fact]
    public void An_official_build_without_a_known_flavor_cannot_update() =>
        Assert.False(BuildInfo.Create("true", "portable", "1.0.57").CanUpdate);

    [Fact]
    public void The_commit_suffix_is_stripped_from_the_displayed_version()
    {
        var build = BuildInfo.Create("true", "framework-dependent", "1.0.57+3f2a1bc");

        Assert.Equal("1.0.57", build.DisplayVersion);
        Assert.Equal(BuildFlavor.FrameworkDependent, build.Flavor);
        Assert.Equal(new Version(1, 0, 57, 0), build.Version);
    }

    [Fact]
    public void An_assembly_without_the_metadata_reads_as_a_development_build() =>
        Assert.False(BuildInfo.FromAssembly(Assembly.GetExecutingAssembly()).CanUpdate);

    [Fact]
    public void A_release_document_is_read()
    {
        var release = ReleaseInfo.Parse(Releases.Json(Releases.Tag, false, "Dongled-1.0.57-win-x64.zip", "SHA256SUMS"));

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 0, 57, 0), release.Version);
        Assert.Equal("1.0.57", release.VersionText);
        Assert.NotNull(release.FindAsset("SHA256SUMS"));
        Assert.Equal(new Uri("https://github.com/navrozashvili/Dongled/releases/tag/v1.0.57"), release.ReleaseNotesUrl);
    }

    [Fact]
    public void A_prerelease_is_refused() =>
        Assert.Null(ReleaseInfo.Parse(Releases.Json(Releases.Tag, true)));

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\": \"nightly\"}")]
    [InlineData("{\"tag_name\": \"v1.0.57-rc1\"}")]
    public void A_document_that_is_not_a_usable_release_is_refused(string json) =>
        Assert.Null(ReleaseInfo.Parse(json));

    [Fact]
    public void An_asset_hosted_anywhere_but_this_repositorys_releases_is_ignored()
    {
        const string json = """
            {
              "tag_name": "v1.0.57",
              "assets": [
                { "name": "Dongled-1.0.57-win-x64.zip", "browser_download_url": "https://evil.example/Dongled-1.0.57-win-x64.zip" },
                { "name": "SHA256SUMS", "browser_download_url": "http://github.com/navrozashvili/Dongled/releases/download/v1.0.57/SHA256SUMS" },
                { "name": "other", "browser_download_url": "https://github.com/someone/else/releases/download/v1.0.57/other" }
              ]
            }
            """;

        var release = ReleaseInfo.Parse(json);

        Assert.NotNull(release);
        Assert.Empty(release.Assets);
    }

    [Fact]
    public void A_checksum_is_found_by_exact_name()
    {
        var hash = new string('a', 64);
        var other = new string('b', 64);
        var sums = $"{other}  Dongled-1.0.57-win-x64-framework-dependent.zip\n{hash.ToUpperInvariant()} *Dongled-1.0.57-win-x64.zip\r\n";

        Assert.Equal(hash, Sha256Sums.Find(sums, "Dongled-1.0.57-win-x64.zip"));
        Assert.Equal(other, Sha256Sums.Find(sums, "Dongled-1.0.57-win-x64-framework-dependent.zip"));
        Assert.Null(Sha256Sums.Find(sums, "plugins.json"));
    }

    [Fact]
    public void A_name_listed_twice_has_no_checksum()
    {
        var sums = $"{new string('a', 64)}  x.zip\n{new string('b', 64)}  x.zip\n";

        Assert.Null(Sha256Sums.Find(sums, "x.zip"));
    }

    [Theory]
    [InlineData(BuildFlavor.SelfContained, "Dongled-1.0.57-win-x64.zip")]
    [InlineData(BuildFlavor.FrameworkDependent, "Dongled-1.0.57-win-x64-framework-dependent.zip")]
    public void Each_flavor_names_its_own_asset(BuildFlavor flavor, string expected) =>
        Assert.Equal(expected, UpdateInstaller.AssetNameFor(flavor, "1.0.57"));
}
