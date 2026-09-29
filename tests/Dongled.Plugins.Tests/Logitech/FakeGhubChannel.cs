using Dongled.Plugin.Logitech;

namespace Dongled.Plugins.Tests.Logitech;

/// <summary>An agent that is whatever the test says it is, one connection attempt at a time.</summary>
internal sealed class FakeGhubChannelFactory : IGhubChannelFactory
{
    private readonly Queue<FakeGhubChannel> _script = new();
    private readonly List<FakeGhubChannel> _created = [];
    private readonly object _gate = new();

    /// <summary>Every channel handed out, in order.</summary>
    public IReadOnlyList<FakeGhubChannel> Created
    {
        get
        {
            lock (_gate)
            {
                return [.. _created];
            }
        }
    }

    /// <summary>The next connection attempt fails, standing in for an agent that is not running.</summary>
    public FakeGhubChannelFactory ThenRefused(string message = "connection refused")
    {
        lock (_gate)
        {
            _script.Enqueue(FakeGhubChannel.Refusing(message));
        }

        return this;
    }

    /// <summary>
    /// The next connection succeeds and delivers <paramref name="messages"/> in order, then behaves as
    /// the agent closing the connection.
    /// </summary>
    public FakeGhubChannelFactory ThenConversation(params string?[] messages)
    {
        lock (_gate)
        {
            _script.Enqueue(FakeGhubChannel.Delivering(messages));
        }

        return this;
    }

    /// <summary>
    /// The next connection succeeds, delivers <paramref name="messages"/>, then stays open and silent.
    /// Use for a test that wants to assert and then stop rather than watch a reconnect.
    /// </summary>
    public FakeGhubChannelFactory ThenOpenConversation(params string[] messages)
    {
        lock (_gate)
        {
            _script.Enqueue(FakeGhubChannel.DeliveringThenSilent(messages));
        }

        return this;
    }

    /// <summary>
    /// The next connection succeeds and hands out an already-constructed channel. Use when the test
    /// needs to keep its own reference to the channel - to call <see cref="FakeGhubChannel.Deliver"/>
    /// on it while the provider is running, for instance.
    /// </summary>
    public FakeGhubChannelFactory ThenChannel(FakeGhubChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        lock (_gate)
        {
            _script.Enqueue(channel);
        }

        return this;
    }

    public IGhubChannel Create()
    {
        lock (_gate)
        {
            // Past the end of the script the agent is simply not there, so a provider that keeps
            // reconnecting does something harmless rather than replaying the last entry.
            var channel = _script.Count > 0 ? _script.Dequeue() : FakeGhubChannel.Refusing("no agent");
            _created.Add(channel);
            return channel;
        }
    }
}

/// <summary>One scripted connection.</summary>
internal sealed class FakeGhubChannel : IGhubChannel
{
    private readonly Queue<string?> _messages = new();
    private readonly List<string> _sent = [];
    private readonly object _gate = new();
    private readonly string? _refusal;
    private readonly bool _silentWhenDrained;
    private bool _open;
    private TaskCompletionSource? _awaiting;

    private FakeGhubChannel(string? refusal, bool silentWhenDrained, IEnumerable<string?> messages)
    {
        _refusal = refusal;
        _silentWhenDrained = silentWhenDrained;
        foreach (var message in messages)
        {
            _messages.Enqueue(message);
        }
    }

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _open;
            }
        }
    }

    /// <summary>Whether the provider disposed this channel.</summary>
    public bool Disposed { get; private set; }

    /// <summary>Every request the provider sent, in order.</summary>
    public IReadOnlyList<string> Sent
    {
        get
        {
            lock (_gate)
            {
                return [.. _sent];
            }
        }
    }

    public static FakeGhubChannel Refusing(string message) => new(message, false, []);

    public static FakeGhubChannel Delivering(IEnumerable<string?> messages) => new(null, false, messages);

    public static FakeGhubChannel DeliveringThenSilent(IEnumerable<string> messages) =>
        new(null, true, messages);

    public Task ConnectAsync(Uri url, TimeSpan keepAlive, CancellationToken ct)
    {
        if (_refusal is not null)
        {
            return Task.FromException(new InvalidOperationException(_refusal));
        }

        lock (_gate)
        {
            _open = true;
        }

        return Task.CompletedTask;
    }

    public Task SendAsync(string json, CancellationToken ct)
    {
        lock (_gate)
        {
            _sent.Add(json);
        }

        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        while (true)
        {
            TaskCompletionSource wait;
            lock (_gate)
            {
                if (_messages.Count > 0)
                {
                    return _messages.Dequeue();
                }

                if (!_silentWhenDrained)
                {
                    // The agent closed the connection.
                    _open = false;
                    return null;
                }

                // Open and silent: parked until either the provider is stopped or a test calls
                // Deliver, which is what a real idle agent looks like to a pending receive.
                _awaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _awaiting;
            }

            using var registration = ct.Register(static s => ((TaskCompletionSource)s!).TrySetCanceled(), wait);
            await wait.Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Push one more message into an open, silent connection - what the live agent does over time.
    /// Only meaningful on a channel created with <see cref="DeliveringThenSilent"/>, which is the mode
    /// a pending <see cref="ReceiveAsync"/> parks in rather than completing.
    /// </summary>
    public Task Deliver(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_gate)
        {
            _messages.Enqueue(message);
            _awaiting?.TrySetResult();
            _awaiting = null;
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        lock (_gate)
        {
            _open = false;
        }

        return ValueTask.CompletedTask;
    }
}
