using System.Net;
using System.Net.Http;
using Dongled.Core.Configuration;
using Dongled.Core.Tests.Fakes;
using Dongled.Core.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Dongled.Core.Tests.Updates;

public sealed class UpdateServiceTests : IDisposable
{
    private readonly FakeHttpHandler _http = new();
    private readonly HttpClient _client;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly StubConfigStore _config = new();
    private readonly MemoryUpdateStateStore _state = new();
    private readonly TemporaryDirectory _directory = new();

    public UpdateServiceTests() => _client = new HttpClient(_http);

    public void Dispose()
    {
        _client.Dispose();
        _http.Dispose();
        _directory.Dispose();
    }

    private UpdateService Create(BuildInfo? build = null)
    {
        build ??= Releases.Official();
        var client = new GitHubReleaseClient(_client, build.DisplayVersion);
        var installer = new UpdateInstaller(
            client,
            build,
            _directory.Combine("app"),
            _directory.Combine("staging"),
            "Dongled.exe",
            NullLogger.Instance);

        return new UpdateService(build, client, installer, _config, _state, _time, NullLogger<UpdateService>.Instance);
    }

    private void Latest(string tag) => _http.Json(UpdateEndpoints.LatestRelease, Releases.Json(tag, false));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private int Queries => _http.RequestsTo(UpdateEndpoints.LatestRelease);

    [Fact]
    public async Task A_newer_release_is_offered()
    {
        Latest("v1.0.57");
        var service = Create();

        await service.CheckIfDueAsync(Token);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, service.Current.Status);
        Assert.Equal("1.0.57", service.Current.AvailableVersion);
        Assert.Equal(new Uri("https://github.com/navrozashvili/Dongled/releases/tag/v1.0.57"), service.Current.ReleaseNotesUrl);
        Assert.Equal("1.0.57", _state.State.LatestVersion);
        Assert.Equal(_time.GetUtcNow(), _state.State.LastCheckedUtc);
    }

    [Theory]
    [InlineData("v1.0.50")]
    [InlineData("v1.0.49")]
    public async Task The_same_or_an_older_release_is_not_offered(string tag)
    {
        Latest(tag);
        var service = Create();

        await service.CheckIfDueAsync(Token);

        Assert.Equal(UpdateCheckStatus.UpToDate, service.Current.Status);
        Assert.Null(service.Current.AvailableVersion);
    }

    [Fact]
    public async Task A_commit_suffix_on_the_running_version_does_not_make_the_same_release_look_newer()
    {
        Latest("v1.0.57");
        var service = Create(BuildInfo.Create("true", "self-contained", "1.0.57+3f2a1bc"));

        await service.CheckIfDueAsync(Token);

        Assert.Equal(UpdateCheckStatus.UpToDate, service.Current.Status);
    }

    [Fact]
    public async Task The_request_carries_a_user_agent_and_asks_for_the_github_media_type()
    {
        Latest("v1.0.57");

        await Create().CheckIfDueAsync(Token);

        var request = Assert.Single(_http.Requests);
        Assert.Contains("Dongled/1.0.50", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        Assert.Contains(request.Headers.Accept, accept => accept.MediaType == "application/vnd.github+json");
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task Opening_again_within_the_interval_does_not_ask_again()
    {
        Latest("v1.0.57");
        var service = Create();

        await service.CheckIfDueAsync(Token);
        _time.Advance(TimeSpan.FromHours(5));
        await service.CheckIfDueAsync(Token);

        Assert.Equal(1, Queries);

        _time.Advance(TimeSpan.FromHours(1));
        await service.CheckIfDueAsync(Token);

        Assert.Equal(2, Queries);
    }

    [Fact]
    public async Task The_interval_survives_a_restart()
    {
        Latest("v1.0.57");
        await Create().CheckIfDueAsync(Token);

        _time.Advance(TimeSpan.FromHours(1));
        var restarted = Create();
        await restarted.CheckIfDueAsync(Token);

        Assert.Equal(1, Queries);
        Assert.Equal("1.0.57", restarted.Current.AvailableVersion);
    }

    [Fact]
    public async Task A_failed_check_is_retried_sooner_than_a_successful_one()
    {
        _http.Offline = true;
        var service = Create();

        await service.CheckIfDueAsync(Token);
        _time.Advance(TimeSpan.FromMinutes(59));
        await service.CheckIfDueAsync(Token);

        Assert.Single(_http.Requests);

        _time.Advance(TimeSpan.FromMinutes(1));
        await service.CheckIfDueAsync(Token);

        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task Check_now_ignores_the_interval()
    {
        Latest("v1.0.57");
        var service = Create();

        await service.CheckIfDueAsync(Token);
        await service.CheckNowAsync(Token);

        Assert.Equal(2, Queries);
    }

    [Fact]
    public async Task With_the_setting_off_opening_the_window_makes_no_request()
    {
        Latest("v1.0.57");
        _config.Config.App.CheckForUpdates = false;
        var service = Create();

        await service.CheckIfDueAsync(Token);

        Assert.Empty(_http.Requests);
        Assert.Equal(0, _state.SaveCount);
    }

    [Fact]
    public async Task With_the_setting_off_check_now_still_works_because_the_user_asked()
    {
        Latest("v1.0.57");
        _config.Config.App.CheckForUpdates = false;
        var service = Create();

        await service.CheckNowAsync(Token);

        Assert.Equal(1, Queries);
    }

    [Fact]
    public async Task A_development_build_never_makes_a_request_at_all()
    {
        Latest("v1.0.57");
        var service = Create(BuildInfo.Create("false", "self-contained", "0.0.0-dev"));

        service.NotifyWindowOpened();
        await service.CheckIfDueAsync(Token);
        await service.CheckNowAsync(Token);
        await Assert.ThrowsAsync<UpdateException>(() => service.InstallAsync(null, Token));

        Assert.False(service.IsSupported);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task A_build_with_no_metadata_never_makes_a_request()
    {
        Latest("v1.0.57");
        var service = Create(BuildInfo.Create(null, null, "1.0.50"));

        await service.CheckNowAsync(Token);

        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Offline_the_automatic_check_is_silent()
    {
        _http.Offline = true;
        var service = Create();

        await service.CheckIfDueAsync(Token);

        Assert.Equal(UpdateCheckStatus.NeverChecked, service.Current.Status);
        Assert.Null(service.Current.Problem);
        Assert.False(_state.State.LastCheckSucceeded);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_rate_limit_or_server_error_is_silent_too(HttpStatusCode status)
    {
        _http.Status(UpdateEndpoints.LatestRelease, status);
        var service = Create();

        await service.CheckIfDueAsync(Token);

        Assert.Equal(UpdateCheckStatus.NeverChecked, service.Current.Status);
    }

    [Fact]
    public async Task Bad_json_is_silent()
    {
        _http.Json(UpdateEndpoints.LatestRelease, "{ not json");
        var service = Create();

        await service.CheckIfDueAsync(Token);

        Assert.Equal(UpdateCheckStatus.NeverChecked, service.Current.Status);
    }

    [Fact]
    public async Task A_failed_automatic_check_keeps_what_was_known()
    {
        Latest("v1.0.57");
        var service = Create();
        await service.CheckIfDueAsync(Token);

        _http.Offline = true;
        _time.Advance(TimeSpan.FromHours(7));
        await service.CheckIfDueAsync(Token);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, service.Current.Status);
        Assert.Equal("1.0.57", service.Current.AvailableVersion);
    }

    [Fact]
    public async Task A_failed_manual_check_says_why()
    {
        _http.Offline = true;
        var service = Create();

        await service.CheckNowAsync(Token);

        Assert.Equal(UpdateCheckStatus.Failed, service.Current.Status);
        Assert.Contains("GitHub could not be reached", service.Current.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dismissing_hides_the_offer_until_a_newer_release()
    {
        Latest("v1.0.57");
        var service = Create();
        await service.CheckIfDueAsync(Token);

        service.Dismiss();

        Assert.True(service.Current.IsDismissed);
        Assert.True(Create().Current.IsDismissed);

        Latest("v1.0.58");
        await service.CheckNowAsync(Token);

        Assert.Equal("1.0.58", service.Current.AvailableVersion);
        Assert.False(service.Current.IsDismissed);
    }

    [Fact]
    public async Task Changes_are_announced()
    {
        Latest("v1.0.57");
        var service = Create();
        var raised = 0;
        service.Changed += (_, _) => raised++;

        await service.CheckNowAsync(Token);

        Assert.True(raised >= 2);
    }
}
