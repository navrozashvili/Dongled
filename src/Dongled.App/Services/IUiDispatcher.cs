namespace Dongled.App.Services;

/// <summary>The UI thread, as view models see it: somewhere to run work and to host timers.</summary>
/// <remarks>
/// View models take this rather than a <c>DispatcherQueue</c> so they can be constructed and driven
/// in tests, where there is no XAML runtime.
/// </remarks>
internal interface IUiDispatcher
{
    /// <summary>Queue <paramref name="action"/> to run on the UI thread.</summary>
    /// <returns>Whether it was queued. False once the thread is shutting down.</returns>
    bool TryEnqueue(Action action);

    /// <summary>Create a stopped timer whose ticks run on the UI thread.</summary>
    /// <param name="interval">Time between <see cref="IUiTimer.Start"/> and the first tick.</param>
    /// <param name="isRepeating">Whether it keeps ticking at that interval.</param>
    /// <param name="tick">What each tick does.</param>
    IUiTimer CreateTimer(TimeSpan interval, bool isRepeating, Action tick);
}

/// <summary>A timer created by <see cref="IUiDispatcher.CreateTimer"/>.</summary>
internal interface IUiTimer : IDisposable
{
    /// <summary>Start, or restart from zero if already running.</summary>
    void Start();

    /// <summary>Stop without ticking. Harmless if not running.</summary>
    void Stop();
}
