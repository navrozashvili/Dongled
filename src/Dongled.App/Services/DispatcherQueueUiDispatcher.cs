using Microsoft.UI.Dispatching;

namespace Dongled.App.Services;

/// <summary><see cref="IUiDispatcher"/> over a WinUI <see cref="DispatcherQueue"/>.</summary>
internal sealed class DispatcherQueueUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    /// <param name="queue">The UI thread's queue.</param>
    public DispatcherQueueUiDispatcher(DispatcherQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        _queue = queue;
    }

    /// <inheritdoc />
    public bool TryEnqueue(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return _queue.TryEnqueue(() => action());
    }

    /// <inheritdoc />
    public IUiTimer CreateTimer(TimeSpan interval, bool isRepeating, Action tick)
    {
        ArgumentNullException.ThrowIfNull(tick);

        return new Timer(_queue.CreateTimer(), interval, isRepeating, tick);
    }

    private sealed class Timer : IUiTimer
    {
        private readonly DispatcherQueueTimer _timer;
        private readonly Action _tick;

        public Timer(DispatcherQueueTimer timer, TimeSpan interval, bool isRepeating, Action tick)
        {
            _timer = timer;
            _tick = tick;

            _timer.Interval = interval;
            _timer.IsRepeating = isRepeating;
            _timer.Tick += OnTick;
        }

        public void Start() => _timer.Start();

        public void Stop() => _timer.Stop();

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
        }

        private void OnTick(DispatcherQueueTimer sender, object args) => _tick();
    }
}
