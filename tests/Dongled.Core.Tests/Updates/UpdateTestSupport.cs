using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dongled.Core.Configuration;
using Dongled.Core.Updates;

namespace Dongled.Core.Tests.Updates;

/// <summary>An HTTP handler that answers from a table and records every request.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>When set, every request fails as an unreachable network would.</summary>
    public bool Offline { get; set; }

    public int RequestsTo(Uri uri) => Requests.Count(request => request.RequestUri == uri);

    public FakeHttpHandler Route(Uri uri, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes[uri.AbsoluteUri] = respond;
        return this;
    }

    public FakeHttpHandler Json(Uri uri, string json) =>
        Route(uri, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    public FakeHttpHandler Bytes(Uri uri, byte[] bytes) =>
        Route(uri, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

    public FakeHttpHandler Status(Uri uri, HttpStatusCode status) =>
        Route(uri, _ => new HttpResponseMessage(status));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (Offline)
        {
            throw new HttpRequestException("No route to host.");
        }

        if (request.RequestUri is { } uri && _routes.TryGetValue(uri.AbsoluteUri, out var respond))
        {
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
    }
}

/// <summary>An in-memory <see cref="IUpdateStateStore"/>.</summary>
internal sealed class MemoryUpdateStateStore : IUpdateStateStore
{
    public UpdateCheckState State { get; set; } = new();

    public int SaveCount { get; private set; }

    public UpdateCheckState LoadUpdateState() => Clone(State);

    public void SaveUpdateState(UpdateCheckState state)
    {
        State = Clone(state);
        SaveCount++;
    }

    private static UpdateCheckState Clone(UpdateCheckState state) =>
        JsonSerializer.Deserialize<UpdateCheckState>(JsonSerializer.Serialize(state))!;
}

/// <summary>Builds the GitHub release document and its assets.</summary>
internal static class Releases
{
    public const string Tag = "v1.0.57";

    public static Uri Download(string name, string tag = Tag) =>
        new($"https://github.com/{UpdateEndpoints.Repository}/releases/download/{tag}/{name}");

    public static string Json(string tag, bool prerelease, params string[] assets) =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft = false,
            prerelease,
            html_url = $"https://github.com/{UpdateEndpoints.Repository}/releases/tag/{tag}",
            assets = assets.Select(name => new { name, browser_download_url = Download(name, tag).AbsoluteUri, size = 0 }),
        });

    public static BuildInfo Official(string version = "1.0.50", BuildFlavor flavor = BuildFlavor.SelfContained) =>
        BuildInfo.Create("true", flavor == BuildFlavor.SelfContained ? "self-contained" : "framework-dependent", version);

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>A zip holding the given files, keyed by path relative to the root with forward slashes.</summary>
    public static byte[] Zip(IReadOnlyDictionary<string, string> files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, contents) in files)
            {
                var entry = archive.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(contents);
            }
        }

        return buffer.ToArray();
    }
}

/// <summary>A temporary directory deleted at the end of the test.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dongled-upd-" + Guid.NewGuid().ToString("N"));

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Write(string relative, string contents)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
    }

    public string Read(string relative) => File.ReadAllText(Combine(relative));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
