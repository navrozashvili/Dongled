using System.Net.WebSockets;
using System.Text;

namespace Dongled.Plugin.Logitech;

/// <summary>Makes real websocket channels.</summary>
internal sealed class ClientWebSocketChannelFactory : IGhubChannelFactory
{
    public IGhubChannel Create() => new ClientWebSocketChannel();
}

/// <summary>
/// The real agent connection, through <see cref="ClientWebSocket"/>.
/// </summary>
/// <remarks>
/// Deliberately thin: it moves bytes and does not decide. No unit test covers this type - a mock of a
/// framework socket would only prove the mock works - so the hardware smoke test against a running
/// G HUB is its evidence.
/// </remarks>
internal sealed class ClientWebSocketChannel : IGhubChannel
{
    /// <summary>
    /// One read's buffer. The measured <c>/devices/list</c> reply is about 40 KB for three devices, so a
    /// message arriving in several frames is the normal case rather than an edge case.
    /// </summary>
    private const int ReceiveBufferSize = 64 * 1024;

    private readonly ClientWebSocket _socket = new();

    public bool IsOpen => _socket.State == WebSocketState.Open;

    public async Task ConnectAsync(Uri url, TimeSpan keepAlive, CancellationToken ct)
    {
        // Both of these are undocumented vendor facts: lghub_agent REQUIRES the "json" subprotocol,
        // and setting an Origin header makes it refuse the connection.
        _socket.Options.AddSubProtocol("json");
        _socket.Options.KeepAliveInterval = keepAlive;

        await _socket.ConnectAsync(url, ct).ConfigureAwait(false);
    }

    public async Task SendAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct)
            .ConfigureAwait(false);
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            // The span overload, not Write(byte[], int, int): CA1849 flags the latter inside an async
            // method as synchronously blocking, which a MemoryStream never does.
            message.Write(buffer.AsSpan(0, result.Count));

            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(message.ToArray());
    }

    public ValueTask DisposeAsync()
    {
        // No CloseAsync handshake: it needs a live socket and a token, and by the time this runs the
        // connection has usually already failed or the host is shutting down. Disposing releases the
        // socket either way, and the agent tolerates a client that simply goes away.
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
