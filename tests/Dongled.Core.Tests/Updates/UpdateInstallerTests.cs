using System.IO;
using System.Net.Http;
using System.Text;
using Dongled.Core.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dongled.Core.Tests.Updates;

public sealed class UpdateInstallerTests : IDisposable
{
    private const string SelfContained = "Dongled-1.0.57-win-x64.zip";
    private const string FrameworkDependent = "Dongled-1.0.57-win-x64-framework-dependent.zip";

    private readonly FakeHttpHandler _http = new();
    private readonly HttpClient _client;
    private readonly TemporaryDirectory _root = new();

    public UpdateInstallerTests()
    {
        _client = new HttpClient(_http);

        _root.Write(@"app\Dongled.exe", "old exe");
        _root.Write(@"app\Dongled.dll", "old dll");
        Directory.CreateDirectory(_root.Combine("staging"));
    }

    public void Dispose()
    {
        _client.Dispose();
        _http.Dispose();
        _root.Dispose();
    }

    private static byte[] Package(string marker) => Releases.Zip(new Dictionary<string, string>
    {
        ["Dongled.exe"] = $"{marker} exe",
        ["Dongled.dll"] = $"{marker} dll",
        ["Plugins/HyperXHid/Dongled.Plugin.HyperXHid.dll"] = $"{marker} plugin",
    });

    private UpdateInstaller Create(BuildFlavor flavor = BuildFlavor.SelfContained)
    {
        var build = Releases.Official(flavor: flavor);
        return new UpdateInstaller(
            new GitHubReleaseClient(_client, build.DisplayVersion),
            build,
            _root.Combine("app"),
            _root.Combine("staging"),
            "Dongled.exe",
            NullLogger.Instance);
    }

    /// <summary>Publish a release holding both flavors, each with its own contents, and their checksums.</summary>
    private void Publish(string? sums = null, params string[] omit)
    {
        var selfContained = Package("self-contained");
        var frameworkDependent = Package("framework-dependent");

        var assets = new[] { SelfContained, FrameworkDependent, Sha256Sums.FileName, "plugins.json" }
            .Except(omit)
            .ToArray();

        _http.Json(UpdateEndpoints.LatestRelease, Releases.Json(Releases.Tag, false, assets));
        _http.Bytes(Releases.Download(SelfContained), selfContained);
        _http.Bytes(Releases.Download(FrameworkDependent), frameworkDependent);
        _http.Bytes(
            Releases.Download(Sha256Sums.FileName),
            Encoding.UTF8.GetBytes(sums ?? $"{Releases.Sha256(selfContained)}  {SelfContained}\n{Releases.Sha256(frameworkDependent)}  {FrameworkDependent}\n"));
    }

    [Fact]
    public async Task The_self_contained_build_installs_the_self_contained_asset()
    {
        Publish();

        var release = await Create(BuildFlavor.SelfContained).InstallAsync(null, CancellationToken.None);

        Assert.Equal("1.0.57", release.VersionText);
        Assert.Equal("self-contained exe", _root.Read(@"app\Dongled.exe"));
        Assert.Equal("self-contained plugin", _root.Read(@"app\Plugins\HyperXHid\Dongled.Plugin.HyperXHid.dll"));
        Assert.Equal(1, _http.RequestsTo(Releases.Download(SelfContained)));
        Assert.Equal(0, _http.RequestsTo(Releases.Download(FrameworkDependent)));
    }

    [Fact]
    public async Task The_framework_dependent_build_installs_the_framework_dependent_asset()
    {
        Publish();

        await Create(BuildFlavor.FrameworkDependent).InstallAsync(null, CancellationToken.None);

        Assert.Equal("framework-dependent exe", _root.Read(@"app\Dongled.exe"));
        Assert.Equal(0, _http.RequestsTo(Releases.Download(SelfContained)));
    }

    [Fact]
    public async Task The_staging_folder_is_left_empty()
    {
        Publish();

        await Create().InstallAsync(null, CancellationToken.None);

        Assert.Empty(Directory.EnumerateFileSystemEntries(_root.Combine("staging")));
    }

    [Fact]
    public async Task A_checksum_mismatch_is_refused_and_nothing_changes()
    {
        Publish(sums: $"{new string('0', 64)}  {SelfContained}\n");

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Create().InstallAsync(null, CancellationToken.None));

        Assert.Contains("does not match the checksum", ex.Message, StringComparison.Ordinal);
        Assert.Equal("old exe", _root.Read(@"app\Dongled.exe"));
        Assert.False(Directory.Exists(_root.Combine(@"app\Plugins")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root.Combine("staging")));
    }

    [Fact]
    public async Task A_missing_package_is_refused_before_anything_is_downloaded()
    {
        Publish(null, SelfContained);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Create().InstallAsync(null, CancellationToken.None));

        Assert.Contains(SelfContained, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, _http.RequestsTo(Releases.Download(Sha256Sums.FileName)));
        Assert.Equal("old exe", _root.Read(@"app\Dongled.exe"));
    }

    [Fact]
    public async Task A_release_without_checksums_is_refused()
    {
        Publish(null, Sha256Sums.FileName);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Create().InstallAsync(null, CancellationToken.None));

        Assert.Contains(Sha256Sums.FileName, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, _http.RequestsTo(Releases.Download(SelfContained)));
    }

    [Fact]
    public async Task Checksums_that_do_not_list_the_package_are_refused()
    {
        Publish(sums: $"{new string('0', 64)}  {FrameworkDependent}\n");

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Create().InstallAsync(null, CancellationToken.None));

        Assert.Contains("does not list", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, _http.RequestsTo(Releases.Download(SelfContained)));
    }

    [Fact]
    public async Task A_release_that_is_not_newer_is_not_installed()
    {
        Publish();
        var build = Releases.Official("1.0.57");
        var installer = new UpdateInstaller(
            new GitHubReleaseClient(_client, build.DisplayVersion),
            build,
            _root.Combine("app"),
            _root.Combine("staging"),
            "Dongled.exe",
            NullLogger.Instance);

        await Assert.ThrowsAsync<UpdateException>(() => installer.InstallAsync(null, CancellationToken.None));

        Assert.Equal("old exe", _root.Read(@"app\Dongled.exe"));
    }

    [Fact]
    public async Task A_package_without_the_executable_at_its_root_is_refused()
    {
        var package = Releases.Zip(new Dictionary<string, string> { ["Dongled/Dongled.exe"] = "nested" });
        _http.Json(UpdateEndpoints.LatestRelease, Releases.Json(Releases.Tag, false, SelfContained, Sha256Sums.FileName));
        _http.Bytes(Releases.Download(SelfContained), package);
        _http.Bytes(Releases.Download(Sha256Sums.FileName), Encoding.UTF8.GetBytes($"{Releases.Sha256(package)}  {SelfContained}"));

        await Assert.ThrowsAsync<UpdateException>(() => Create().InstallAsync(null, CancellationToken.None));

        Assert.Equal("old exe", _root.Read(@"app\Dongled.exe"));
    }

    [Fact]
    public async Task Offline_the_install_fails_with_a_sentence()
    {
        _http.Offline = true;

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Create().InstallAsync(null, CancellationToken.None));

        Assert.Contains("GitHub could not be reached", ex.Message, StringComparison.Ordinal);
    }
}
