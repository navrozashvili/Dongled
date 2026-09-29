using Dongled.App.Services;

namespace Dongled.App.Tests.Fakes;

/// <summary>
/// A UI thread that runs queued work immediately and whose timers tick only when a test says so.
/// </summary>
internal sealed class ManualUiDispatcher : IUiDispatcher
{
    public List<ManualTimer> Timers { get; } = [];

    public bool TryEnqueue(Action action)
    {
        action();
        return true;
    }

    public IUiTimer CreateTimer(TimeSpan interval, bool isRepeating, Action tick)
    {
        var timer = new ManualTimer(interval, tick);
        Timers.Add(timer);
        return timer;
    }

    /// <summary>Tick every running timer once, as if its interval had elapsed.</summary>
    public void ElapseAll()
    {
        foreach (var timer in Timers.Where(timer => timer.IsRunning).ToList())
        {
            timer.Fire();
        }
    }
}

internal sealed class ManualTimer(TimeSpan interval, Action tick) : IUiTimer
{
    public TimeSpan Interval { get; } = interval;

    public bool IsRunning { get; private set; }

    public bool IsDisposed { get; private set; }

    public void Start() => IsRunning = !IsDisposed;

    public void Stop() => IsRunning = false;

    public void Dispose()
    {
        IsRunning = false;
        IsDisposed = true;
    }

    public void Fire() => tick();
}
