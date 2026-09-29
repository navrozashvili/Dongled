using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Dongled.Core.Updates;

/// <summary>
/// Asks GitHub for the latest release and downloads its files. Every failure surfaces as an
/// <see cref="UpdateException"/> whose message can be shown to a user.
/// </summary>
/// <remarks>
/// Unauthenticated: there is no token to leak and nothing identifying is sent beyond the product
/// name and version in the User-Agent GitHub requires.
/// </remarks>
public sealed class GitHubReleaseClient
{
    /// <summary>How long the release query may take. Short, because a check the user did not ask for must never be noticed.</summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a single download may take.</summary>
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly ProductInfoHeaderValue _product;
    private readonly ProductInfoHeaderValue _comment = new("(+https://github.com/" + UpdateEndpoints.Repository + ")");

    /// <param name="http">
    /// The client to send through. Its own timeout is not relied on; each call applies its own.
    /// </param>
    /// <param name="version">The running version, for the User-Agent.</param>
    public GitHubReleaseClient(HttpClient http, string version)
    {
        ArgumentNullException.ThrowIfNull(http);

        _http = http;

        // A version with characters a product token cannot hold is dropped rather than letting the
        // header throw; GitHub needs a User-Agent, not a particular version in it.
        _product = ProductInfoHeaderValue.TryParse($"Dongled/{version}", out var product)
            ? product
            : new ProductInfoHeaderValue("Dongled", null);
    }

    /// <summary>The newest full release.</summary>
    /// <exception cref="UpdateException">GitHub could not be reached or its answer could not be used.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<ReleaseInfo> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(QueryTimeout);

        using var request = CreateRequest(UpdateEndpoints.LatestRelease);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        string body;
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            ThrowForStatus(response.StatusCode, "a published release of Dongled");
            body = await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (Exception ex) when (Translate(ex, "check for updates", cancellationToken) is { } translated)
        {
            throw translated;
        }

        return ReleaseInfo.Parse(body)
            ?? throw new UpdateException("GitHub's answer about the latest release could not be read.");
    }

    /// <summary>Download a small text asset into memory.</summary>
    /// <param name="asset">What to download.</param>
    /// <param name="maximumBytes">Refuse anything larger.</param>
    /// <param name="cancellationToken">Stops the download.</param>
    public async Task<string> DownloadTextAsync(ReleaseAsset asset, int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);

        using var buffer = new MemoryStream();
        await DownloadAsync(asset, buffer, maximumBytes, cancellationToken);

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>Download an asset to a file and return its SHA-256 as lowercase hex.</summary>
    /// <param name="asset">What to download.</param>
    /// <param name="path">Where to write it. Created, or overwritten.</param>
    /// <param name="maximumBytes">Refuse anything larger.</param>
    /// <param name="progress">Receives the fraction downloaded, when the size is known.</param>
    /// <param name="cancellationToken">Stops the download.</param>
    public async Task<string> DownloadFileAsync(
        ReleaseAsset asset,
        string path,
        long maximumBytes,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await using var hashing = new HashingStream(file, hash);
            await DownloadAsync(asset, hashing, maximumBytes, cancellationToken, progress);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void ThrowForStatus(HttpStatusCode status, string what)
    {
        if ((int)status is >= 200 and < 300)
        {
            return;
        }

        throw status switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests =>
                new UpdateException("GitHub is limiting requests from this network for now. Try again later."),
            HttpStatusCode.NotFound =>
                new UpdateException($"GitHub could not find {what}."),
            _ => new UpdateException($"GitHub answered {(int)status} when asked for {what}."),
        };
    }

    /// <summary>Turn a transport failure into the one sentence a user sees; null lets a real cancellation through.</summary>
    private static UpdateException? Translate(Exception ex, string action, CancellationToken cancellationToken) => ex switch
    {
        UpdateException => null,
        OperationCanceledException when cancellationToken.IsCancellationRequested => null,
        OperationCanceledException => new UpdateException($"GitHub did not answer in time, so Dongled could not {action}.", ex),
        HttpRequestException => new UpdateException($"GitHub could not be reached, so Dongled could not {action}.", ex),
        IOException => new UpdateException($"The connection to GitHub failed, so Dongled could not {action}.", ex),
        _ => null,
    };

    private HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(_product);
        request.Headers.UserAgent.Add(_comment);
        return request;
    }

    private async Task DownloadAsync(
        ReleaseAsset asset,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken,
        IProgress<double>? progress = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);

        using var request = CreateRequest(asset.DownloadUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            ThrowForStatus(response.StatusCode, asset.Name);

            // GitHub redirects a release download to its storage host. Wherever it ends up, it
            // must still be HTTPS.
            if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
            {
                throw new UpdateException($"The download of {asset.Name} was redirected away from HTTPS and was stopped.");
            }

            var length = response.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : (long?)null);
            if (length > maximumBytes)
            {
                throw new UpdateException($"{asset.Name} is larger than expected and was not downloaded.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);

            var buffer = new byte[81920];
            long total = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
            {
                total += read;
                if (total > maximumBytes)
                {
                    throw new UpdateException($"{asset.Name} is larger than expected and the download was stopped.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), timeout.Token);

                if (length is > 0)
                {
                    progress?.Report((double)total / length.Value);
                }
            }
        }
        catch (Exception ex) when (Translate(ex, $"download {asset.Name}", cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>Hashes what passes through on its way to a file, so the file is never read back to be checked.</summary>
    private sealed class HashingStream(Stream inner, IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            hash.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            hash.AppendData(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken);
        }
    }
}
