using Dongled.App.Services;

namespace Dongled.App.Mvvm;

/// <summary>
/// Runs an action once activity pauses, rather than on every change, and can be made to run it
/// immediately when waiting is no longer acceptable.
/// </summary>
internal sealed class Debouncer : IDisposable
{
    private readonly IUiTimer _timer;
    private readonly Action _action;
    private bool _pending;

    /// <param name="dispatcher">Hosts the timer.</param>
    /// <param name="delay">How long activity must pause before <paramref name="action"/> runs.</param>
    /// <param name="action">What to run.</param>
    public Debouncer(IUiDispatcher dispatcher, TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(action);

        _action = action;
        _timer = dispatcher.CreateTimer(delay, isRepeating: false, Flush);
    }

    /// <summary>Whether the action is waiting to run.</summary>
    public bool IsPending => _pending;

    /// <summary>Note activity, pushing the action back by the full delay.</summary>
    public void Trigger()
    {
        _pending = true;

        // Restarting rather than leaving it running is what makes this a debounce rather than a
        // throttle: the action runs once activity stops, not periodically while it continues.
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Run the action now if it is waiting; otherwise do nothing.</summary>
    public void Flush()
    {
        _timer.Stop();

        if (!_pending)
        {
            return;
        }

        _pending = false;
        _action();
    }

    /// <summary>Stop the timer without running anything. Call <see cref="Flush"/> first to keep pending work.</summary>
    public void Dispose() => _timer.Dispose();
}
