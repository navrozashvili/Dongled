namespace Dongled.Plugin.Logitech;

/// <summary>Makes a channel per connection attempt.</summary>
/// <remarks>
/// A factory rather than a single channel because a <c>ClientWebSocket</c> is single-use: once it has
/// closed or faulted it cannot reconnect, and the reconnect loop needs a fresh one each time.
/// </remarks>
internal interface IGhubChannelFactory
{
    IGhubChannel Create();
}

/// <summary>
/// One connection to the G HUB agent. The seam that keeps <see cref="LogitechGhubProvider"/> testable
/// with no agent running; <see cref="ClientWebSocketChannel"/> is the only implementation that opens a
/// socket.
/// </summary>
internal interface IGhubChannel : IAsyncDisposable
{
    /// <summary>Whether the connection is still usable.</summary>
    bool IsOpen { get; }

    /// <summary>Connect, or throw if the agent is not there.</summary>
    Task ConnectAsync(Uri url, TimeSpan keepAlive, CancellationToken ct);

    /// <summary>Send one message.</summary>
    Task SendAsync(string json, CancellationToken ct);

    /// <summary>Receive one whole message, or null once the agent has closed the connection.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);
}

/// <summary>
/// How long the provider waits for things. Injectable so a test of the reconnect and outage loops does
/// not wait real seconds; production uses <see cref="Default"/>.
/// </summary>
/// <param name="TickInterval">
/// How often the loop wakes to consider a refresh while no message has arrived. Also how promptly
/// <see cref="LogitechGhubProvider.StopAsync"/> returns.
/// </param>
internal sealed record LogitechGhubTimings(TimeSpan TickInterval)
{
    public static LogitechGhubTimings Default { get; } = new(TickInterval: TimeSpan.FromSeconds(1));
}
