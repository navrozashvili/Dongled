using System.Threading;
using System.Threading.Tasks;
using Dongled.Abstractions;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Pipeline;

/// <summary>Where one provider is in its lifecycle.</summary>
public enum ProviderRunState
{
    /// <summary>Constructed; <see cref="ProviderRunner.StartAsync"/> has not been called.</summary>
    NotStarted = 0,

    /// <summary>Watching.</summary>
    Running = 1,

    /// <summary>Start threw or missed its deadline. Not retried until the plugin is re-enabled.</summary>
    Failed = 2,

    /// <summary>Stopped, whether it had been running or had failed.</summary>
    Stopped = 3,
}

/// <summary>
/// Runs one provider's lifecycle: start it inside a deadline, and give it exactly one cleanup
/// call whatever happened.
/// </summary>
/// <remarks>
/// <para>
/// Both halves of the <see cref="IAudioSourceProvider.StopAsync"/> contract live here.
/// <em>Always if start was called</em> means a provider whose start threw still gets its cleanup
/// call, because the natural implementation abandons it and leaks whatever it opened before
/// throwing. <em>Exactly once</em> is what forces a state machine rather than a <c>finally</c> in
/// two places: without it a provider that missed its start deadline would get one cleanup call
/// from the timeout path and a second from shutdown.
/// </para>
/// <para>
/// A runner is single use. A provider that failed is not started again, which is what "not
/// retried until the plugin is re-enabled" means: re-enabling produces a new runner.
/// </para>
/// </remarks>
public sealed class ProviderRunner : IDisposable
{
    /// <summary>
    /// How long a provider gets to establish watching, per <see cref="IAudioSourceProvider.StartAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unrelated to <c>SwitchingEngine.PresenceResolutionCeiling</c>, which is five seconds and
    /// governs how long the host waits for presences to become known at startup. Conflating them
    /// is the obvious mistake, and the SDK documentation names only this one.
    /// </para>
    /// <para>
    /// It bounds the <em>task</em> a provider returns, not the call that returns it. A provider
    /// that blocks its own thread inside <see cref="IAudioSourceProvider.StartAsync"/> never lets
    /// <see cref="StartAsync"/> reach the line that starts this timer, so nothing here rescues it.
    /// Measured rather than reasoned: a provider publishing into a queue nobody is draining blocks
    /// on the signal after the queue's capacity and stays blocked, with the runner still reporting
    /// <see cref="ProviderRunState.NotStarted"/>. That is why
    /// <c>PluginHost</c> starts the engine draining before it starts any provider, and why the SDK
    /// tells authors to start their watch loop and return rather than run it here.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan StartDeadline = TimeSpan.FromSeconds(10);

    private readonly IAudioSourceProvider _provider;
    private readonly IProviderContext _context;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _providerId;

    private bool _startCalled;
    private bool _stopCalled;
    private bool _disposed;

    // A ProviderRunState stored as an int so the UI can read it, and the start path, the stop path
    // and shutdown can write it, without a lock.
    private int _state = (int)ProviderRunState.NotStarted;

    /// <param name="provider">The provider to run.</param>
    /// <param name="context">What it publishes through.</param>
    /// <param name="time">Clock for the start deadline, so tests need not wait ten seconds.</param>
    /// <param name="logger">Where lifecycle events are reported.</param>
    public ProviderRunner(
        IAudioSourceProvider provider,
        IProviderContext context,
        TimeProvider time,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _provider = provider;
        _context = context;
        _time = time;
        _logger = logger;

        // Read once here rather than at each log site. IAudioSourceProvider documents Metadata as
        // read before StartAsync, it is a property on a plugin's own type so reading it can throw
        // or be slow, and every later use is a logging argument that must be cheap.
        string? id = null;
        try
        {
            id = provider.Metadata?.Id;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A provider's metadata could not be read; logging will name its type instead.");
        }

        _providerId = string.IsNullOrWhiteSpace(id) ? provider.GetType().Name : id;
    }

    /// <summary>Where this provider is in its lifecycle.</summary>
    public ProviderRunState State => (ProviderRunState)Volatile.Read(ref _state);

    /// <summary>
    /// Start the provider and wait up to <see cref="StartDeadline"/> for it to report that
    /// watching is established.
    /// </summary>
    /// <param name="ct">
    /// Cancelled when the host shuts down. A provider still starting then is stopped rather than
    /// failed, because it did nothing wrong.
    /// </param>
    /// <returns>
    /// True if it is now running; false if it failed, missed the deadline, or was abandoned
    /// because the host shut down.
    /// </returns>
    /// <exception cref="InvalidOperationException">This runner was already started.</exception>
    public async Task<bool> StartAsync(CancellationToken ct)
    {
        if (_startCalled)
        {
            throw new InvalidOperationException(
                "This provider has already been started. A failed provider is not retried until its plugin is re-enabled, which produces a new runner.");
        }

        // Set before the call, not after. The cleanup contract keys off StartAsync having been
        // called, so a provider that throws on the very first line still gets its cleanup call.
        _startCalled = true;

        Task start;
        try
        {
            start = _provider.StartAsync(_context, _stopping.Token) ?? Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Provider {ProviderId} threw while starting.", _providerId);
            await FailAsync(ct);
            return false;
        }

        var deadline = Task.Delay(StartDeadline, _time, ct);
        var finished = await Task.WhenAny(start, deadline);

        // The deadline shares the caller's token, so a shutdown during start completes it too.
        if (finished == deadline && deadline.IsCanceled)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Provider {ProviderId} was still starting when the app began shutting down; it is being stopped.",
                    _providerId);
            }

            // Not the caller's token: it is already cancelled, and handing that to the provider's
            // cleanup invites it to skip releasing what it opened. Shutdown stops providers the
            // same way.
            await StopAsync(CancellationToken.None);
            return false;
        }

        if (finished == deadline)
        {
            _logger.LogError(
                "Provider {ProviderId} did not finish starting within {Deadline}. It is treated as failed and will not be started again until its plugin is re-enabled.",
                _providerId,
                StartDeadline);
            await FailAsync(ct);
            return false;
        }

        if (start.IsFaulted)
        {
            _logger.LogError(start.Exception, "Provider {ProviderId} failed to start.", _providerId);
            await FailAsync(ct);
            return false;
        }

        if (start.IsCanceled)
        {
            _logger.LogWarning("Provider {ProviderId} cancelled its own start.", _providerId);
            await FailAsync(ct);
            return false;
        }

        // Only from NotStarted: a stop that ran while the start was still pending has already
        // released the provider, and it must not now be reported as watching.
        if (Interlocked.CompareExchange(ref _state, (int)ProviderRunState.Running, (int)ProviderRunState.NotStarted)
            != (int)ProviderRunState.NotStarted)
        {
            return false;
        }

        // CA1873 requires the guard: an Information-level call is usually disabled in a release
        // build, and the argument array is allocated whether or not anything records it.
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Provider {ProviderId} is watching.", _providerId);
        }

        return true;
    }

    /// <summary>
    /// Stop the provider, exactly once. Cancels the token it was given first, so a watch loop
    /// unwinds before it is asked to release what it opened.
    /// </summary>
    /// <param name="ct">Cancelled if shutdown is taking too long.</param>
    public async Task StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_stopCalled || !_startCalled)
            {
                // Never started means it opened nothing, so there is nothing to release and
                // nothing the contract promises.
                return;
            }

            _stopCalled = true;

            try
            {
                await _stopping.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // Disposed underneath us during shutdown; the provider is being told to stop
                // regardless.
            }

            try
            {
                await (_provider.StopAsync(ct) ?? Task.CompletedTask);
            }
            catch (Exception ex)
            {
                // A provider that fails to clean up is still stopped as far as the host is
                // concerned. Retrying would violate exactly-once.
                _logger.LogError(ex, "Provider {ProviderId} threw while stopping.", _providerId);
            }

            MarkStoppedUnlessFailed();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_startCalled && !_stopCalled)
        {
            // Not recoverable here, because cleanup is asynchronous and this is not. Reported
            // loudly because it is a host bug that costs a plugin its unmanaged handles.
            _logger.LogError(
                "Provider {ProviderId} was disposed without being stopped. Its cleanup call never happened, which is a host bug.",
                _providerId);
        }

        _stopping.Dispose();
        _gate.Dispose();
    }

    private async Task FailAsync(CancellationToken ct)
    {
        // Set before stopping and never overwritten by it: Failed is the more useful thing for the
        // Plugins page to show, because it says why the provider is not watching.
        Volatile.Write(ref _state, (int)ProviderRunState.Failed);
        await StopAsync(ct);
    }

    private void MarkStoppedUnlessFailed()
    {
        var current = Volatile.Read(ref _state);
        while (current != (int)ProviderRunState.Failed)
        {
            var seen = Interlocked.CompareExchange(ref _state, (int)ProviderRunState.Stopped, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
