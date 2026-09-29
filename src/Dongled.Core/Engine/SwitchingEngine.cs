using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dongled.Abstractions;
using Dongled.Core.Audio;
using Dongled.Core.Configuration;
using Dongled.Core.Pipeline;
using Dongled.Core.Policy;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Engine;

/// <summary>
/// The single consumer at the centre of the app. It exclusively owns the descriptor catalogue,
/// all presence state, the pending returns and the policy; nothing else mutates any of them.
/// </summary>
/// <remarks>
/// <para>
/// Presence reports arrive on provider threads and timer continuations. Rather than locking shared
/// collections against all of them, providers write to a bounded queue, timers write to the same
/// queue, and one thread drains it. There is therefore no lock anywhere in the policy, ordering is deterministic, and a provider that floods
/// throttles itself rather than corrupting a dictionary.
/// </para>
/// <para>
/// Shutdown order matters. Stop every provider first, then call <see cref="CompleteInput"/> or
/// cancel the token given to <see cref="RunAsync"/>. The SDK says a provider may publish and log
/// from inside its own cleanup call, so the queue must still accept writes while providers are
/// stopping.
/// </para>
/// </remarks>
public sealed class SwitchingEngine : ISwitchingEngine, IDisposable
{
    /// <summary>
    /// How long startup waits for every source an enabled rule names to report a presence. A
    /// source still unresolved when this expires is treated as absent.
    /// </summary>
    /// <remarks>
    /// Unrelated to <see cref="ProviderRunner.StartDeadline"/>, which is ten seconds and governs
    /// how long a single provider gets to establish watching. They are different numbers for
    /// different things, and the SDK documentation names only the other one. Deliberately not
    /// configurable.
    /// </remarks>
    public static readonly TimeSpan PresenceResolutionCeiling = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many signals may be queued before a provider waits for room. Large enough that a
    /// provider publishing a normal catalogue never waits, small enough that a runaway provider
    /// is throttled rather than allowed to grow the queue without bound.
    /// </summary>
    private const int QueueCapacity = 256;

    private readonly Channel<EngineSignal> _channel = Channel.CreateBounded<EngineSignal>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    private readonly Dictionary<string, TaskCompletionSource> _awaited = new(StringComparer.OrdinalIgnoreCase);

    private readonly TaskCompletionSource _startupCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Lock _catalogueLock = new();
    private readonly IAudioEndpointService _endpoints;
    private readonly IConfigStore _configStore;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SwitchingEngine> _logger;
    private readonly ILogger _ruleChangeLogger;
    private readonly SourceCatalogue _catalogue = new();
    private readonly SwitchingActions _actions;
    private readonly StabilizationScheduler _scheduler;
    private readonly StartupReconciler _reconciler;

    private AppConfig _config = new();
    private bool _disposed;

    // 1 while a reload request is queued and not yet handled. Written from any thread.
    private int _reloadPending;

    /// <param name="endpoints">Windows audio, or a fake standing in for it.</param>
    /// <param name="configStore">Where rules and settings are read from.</param>
    /// <param name="stateStore">Where captured previous defaults are kept.</param>
    /// <param name="time">Clock for every delay, so tests are instant and deterministic.</param>
    /// <param name="loggerFactory">Used to give each provider its own log category.</param>
    public SwitchingEngine(
        IAudioEndpointService endpoints,
        IConfigStore configStore,
        IStateStore stateStore,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _endpoints = endpoints;
        _configStore = configStore;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SwitchingEngine>();

        // Its own category, outside Engine and Policy, so rule edits reach the log file and the Logs
        // page but not the Status page's activity, which is for what the app did rather than what
        // was configured.
        _ruleChangeLogger = loggerFactory.CreateLogger("Dongled.Core.Configuration.RuleChanges");
        _actions = new SwitchingActions(endpoints, stateStore, loggerFactory.CreateLogger<SwitchingActions>());
        _scheduler = new StabilizationScheduler(
            _channel.Writer,
            time,
            loggerFactory.CreateLogger<StabilizationScheduler>());
        _reconciler = new StartupReconciler(
            endpoints,
            _actions,
            PresenceResolutionCeiling,
            loggerFactory.CreateLogger<StartupReconciler>());
    }

    /// <summary>
    /// Completes once startup reconciliation has run, or immediately if it is switched off. The
    /// UI can wait on this before claiming to show live state.
    /// </summary>
    public Task StartupReconciliationCompleted => _startupCompleted.Task;

    /// <summary>Give one provider its context.</summary>
    /// <param name="providerId">The provider's identifier, used as its log category.</param>
    /// <param name="minimumLogLevel">
    /// That plugin's configured minimum, read on every log call so a change in the UI takes
    /// effect without restarting the provider.
    /// </param>
    public IProviderContext CreateProviderContext(string providerId, Func<LogLevel> minimumLogLevel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(minimumLogLevel);

        var providerLogger = _loggerFactory.CreateLogger($"Provider.{providerId}");
        return new ProviderContext(
            providerId,
            _channel.Writer,
            new ProviderLoggerAdapter(providerLogger, minimumLogLevel),
            _time,
            _logger);
    }

    /// <summary>
    /// Every source any provider currently publishes. Safe to call from any thread; it is a
    /// snapshot taken under a brief lock rather than the consumer's live state.
    /// </summary>
    public IReadOnlyList<AudioSourceDescriptor> KnownSources()
    {
        lock (_catalogueLock)
        {
            return _catalogue.KnownSources();
        }
    }

    /// <summary>
    /// Every source any provider currently publishes, each with its presence. Safe to call from any
    /// thread; it is a snapshot taken under a brief lock.
    /// </summary>
    /// <remarks>
    /// What the Status page shows. Both halves come from one lock acquisition, so a row cannot pair
    /// one source's name with another's presence. A <see cref="Presence.Unknown"/> here is worth
    /// displaying as "not detected yet"; it must never be sent back in as a report, because an
    /// <see cref="Presence.Unknown"/> arriving mid-run overwrites a <see cref="Presence.Present"/>
    /// and the <see cref="Presence.Absent"/> that follows then fires no rule.
    /// </remarks>
    public IReadOnlyList<SourceState> SourceStates()
    {
        lock (_catalogueLock)
        {
            return _catalogue.KnownSourceStates();
        }
    }

    /// <summary>
    /// Ask the consumer to re-read configuration, which the UI does after it saves. Safe to call
    /// from any thread, and never blocks.
    /// </summary>
    /// <remarks>
    /// A dropped request would leave the engine applying stale rules until the next save, so the
    /// request is never discarded. When the queue is full it is written asynchronously instead of
    /// waited for, because the caller is usually the UI thread. Requests coalesce: while one is
    /// queued and not yet handled, another adds nothing, since the queued one reads the file after
    /// this call returns.
    /// </remarks>
    public void RequestConfigurationReload()
    {
        if (Interlocked.Exchange(ref _reloadPending, 1) == 1)
        {
            return;
        }

        var signal = new ConfigurationReloadRequested(_time.GetUtcNow());
        if (!_channel.Writer.TryWrite(signal))
        {
            _ = WriteReloadWhenThereIsRoomAsync(signal);
        }
    }

    /// <summary>
    /// Stop accepting signals, which ends <see cref="RunAsync"/> once the queue has drained.
    /// Call this after every provider has been stopped, never before.
    /// </summary>
    public void CompleteInput() => _channel.Writer.TryComplete();

    /// <summary>
    /// Drain the queue until the input is completed or the token is cancelled. This is the only
    /// thread that touches presence, the catalogue, the pending returns, or the policy.
    /// </summary>
    /// <param name="ct">Cancelling this completes the input; queued signals are still processed.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        _config = _configStore.Load();
        WarnAboutDuplicateSources();
        BeginStartupResolution(ct);

        // Cancelling the token completes the input rather than tearing out of the read, so
        // whatever is already queued is still processed. Reads themselves are not cancellable
        // here on purpose: abandoning queued signals mid-shutdown could leave a rule half applied.
        using var registration = ct.Register(static state => ((SwitchingEngine)state!).CompleteInput(), this);

        await foreach (var signal in _channel.Reader.ReadAllAsync(CancellationToken.None))
        {
            try
            {
                Handle(signal);
            }
            catch (Exception ex)
            {
                // The consumer may never die. It owns every rule's state, so an unhandled failure
                // here would silently stop all switching for the rest of the session.
                _logger.LogError(ex, "Handling {SignalType} failed. The engine continues.", signal.GetType().Name);
            }
        }

        // Nothing else will complete this if resolution was still waiting when shutdown began.
        _startupCompleted.TrySetResult();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CompleteInput();
        _scheduler.Dispose();
    }

    private void Handle(EngineSignal signal)
    {
        switch (signal)
        {
            case SourcesPublished published:
                lock (_catalogueLock)
                {
                    _catalogue.ReplaceSources(published.ProviderId, published.Sources);
                }

                break;

            case PresenceReported reported:
                HandlePresence(reported);
                break;

            case BatteryReported battery:
                lock (_catalogueLock)
                {
                    _catalogue.SetBattery(
                        battery.SourceId,
                        new BatteryReading(battery.Percent, battery.Charge));
                }

                break;

            case StabilizationElapsed elapsed:
                HandleStabilizationElapsed(elapsed);
                break;

            case StartupResolutionFinished:
                HandleStartupResolutionFinished();
                break;

            case ConfigurationReloadRequested:
                // Cleared before reading, not after: a save that lands while the file is being read
                // must queue a fresh request rather than coalesce into one that may have missed it.
                Volatile.Write(ref _reloadPending, 0);
                var previousRules = _config.Rules;
                _config = _configStore.Load();

                if (_ruleChangeLogger.IsEnabled(LogLevel.Information))
                {
                    foreach (var change in RuleChanges.Describe(previousRules, _config.Rules))
                    {
                        _ruleChangeLogger.LogInformation("{RuleChange}", change);
                    }
                }

                WarnAboutDuplicateSources();
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Configuration reloaded: {RuleCount} rule(s).", _config.Rules.Count);
                }

                break;

            default:
                _logger.LogWarning("Ignoring an unrecognised signal of type {SignalType}.", signal.GetType().Name);
                break;
        }
    }

    private void HandlePresence(PresenceReported reported)
    {
        Presence previous;
        lock (_catalogueLock)
        {
            previous = _catalogue.SetPresence(reported.SourceId, reported.Presence);
        }

        if (reported.Presence != Presence.Unknown
            && _awaited.TryGetValue(reported.SourceId, out var resolved))
        {
            resolved.TrySetResult();
        }

        // The SDK promises that a repeated presence produces no additional switching, so a
        // provider that polls does not have to remember what it last said.
        if (reported.Presence == previous)
        {
            return;
        }

        var rules = RulesFor(reported.SourceId);
        if (rules.Count == 0)
        {
            return;
        }

        if (reported.Presence == Presence.Present)
        {
            _scheduler.Cancel(reported.SourceId);
            foreach (var rule in rules)
            {
                Apply(rule, _actions.SwitchToTarget);
            }

            return;
        }

        // Only a source that had been connected schedules a return. Absent arriving from Unknown
        // is a first answer rather than a disconnection: nothing was switched, so there is nothing
        // to come back from, and startup reconciliation is what decides whether the user is
        // already stuck on a device that is switched off.
        if (reported.Presence == Presence.Absent && previous == Presence.Present)
        {
            var delay = TimeSpan.FromSeconds(Math.Max(0, _config.App.DisconnectStabilizationSeconds));
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "{SourceId} disconnected. Waiting {Delay} before applying its rules' disconnect behaviour.",
                    reported.SourceId,
                    delay);
            }

            _scheduler.Schedule(reported.SourceId, delay);
        }
    }

    private void HandleStabilizationElapsed(StabilizationElapsed elapsed)
    {
        if (!_scheduler.TryComplete(elapsed.SourceId, elapsed.Epoch))
        {
            // Superseded or cancelled while this was in the queue, which is how a reconnect inside
            // the window makes an already-queued signal harmless.
            return;
        }

        Presence presence;
        lock (_catalogueLock)
        {
            presence = _catalogue.GetPresence(elapsed.SourceId);
        }

        // Belt and braces with the Cancel below, deliberately. Cancelling stops the timer, but a
        // signal already on the queue when the reconnect arrives cannot be recalled, and this is
        // what makes that one harmless. Either alone satisfies the test; both are kept because
        // they cover different halves of the race.
        if (presence != Presence.Absent)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "{SourceId} is back before its stabilization delay was applied; no return.",
                    elapsed.SourceId);
            }

            return;
        }

        foreach (var rule in RulesFor(elapsed.SourceId))
        {
            Apply(rule, _actions.ApplyDisconnect);
        }
    }

    private void HandleStartupResolutionFinished()
    {
        if (_startupCompleted.Task.IsCompleted)
        {
            return;
        }

        if (_config.App.ApplyRulesOnStartup)
        {
            _reconciler.Reconcile(_config.Rules, PresenceOf, _scheduler.HasPending);
        }

        _startupCompleted.TrySetResult();
    }

    private Presence PresenceOf(string sourceId)
    {
        lock (_catalogueLock)
        {
            return _catalogue.GetPresence(sourceId);
        }
    }

    private void BeginStartupResolution(CancellationToken ct)
    {
        if (!_config.App.ApplyRulesOnStartup)
        {
            _logger.LogInformation("Applying rules at startup is switched off; no reconciliation will run.");
            _startupCompleted.TrySetResult();
            return;
        }

        foreach (var rule in _config.Rules)
        {
            if (rule.Enabled && !string.IsNullOrWhiteSpace(rule.Source.Id))
            {
                _awaited.TryAdd(
                    rule.Source.Id,
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }
        }

        // Built before the watcher starts and never changed afterwards, so the watcher's snapshot
        // and the consumer's lookups need no synchronisation.
        _ = WaitForResolutionAsync(ct);
    }

    private async Task WaitForResolutionAsync(CancellationToken ct)
    {
        var awaited = _awaited.ToArray();

        if (awaited.Length > 0)
        {
            // Waits on a condition rather than a fixed delay, so the normal case finishes in
            // milliseconds and only a silent source costs the full ceiling.
            var everything = Task.WhenAll(awaited.Select(entry => entry.Value.Task));
            var ceiling = Task.Delay(PresenceResolutionCeiling, _time, ct);

            if (await Task.WhenAny(everything, ceiling) == ceiling)
            {
                var unresolved = awaited
                    .Where(entry => !entry.Value.Task.IsCompleted)
                    .Select(entry => entry.Key)
                    .ToArray();

                _logger.LogWarning(
                    "Presence did not resolve within the {Ceiling} ceiling for {UnresolvedCount} source(s): {Unresolved}. They are treated as absent, which can switch away from a device that is in fact connected.",
                    PresenceResolutionCeiling,
                    unresolved.Length,
                    string.Join(", ", unresolved));
            }
        }

        try
        {
            await _channel.Writer.WriteAsync(
                new StartupResolutionFinished(_time.GetUtcNow()),
                CancellationToken.None);
        }
        catch (ChannelClosedException)
        {
            // Shutdown began before resolution finished. RunAsync completes the task itself.
        }
    }

    private async Task WriteReloadWhenThereIsRoomAsync(ConfigurationReloadRequested signal)
    {
        try
        {
            await _channel.Writer.WriteAsync(signal, CancellationToken.None);
        }
        catch (ChannelClosedException)
        {
            // Shutting down. Nothing will apply rules again, so there is nothing to reload for.
            _logger.LogDebug("A configuration reload was requested after the engine stopped; ignored.");
        }
    }

    private List<Rule> RulesFor(string sourceId)
    {
        var matches = new List<Rule>();
        foreach (var rule in _config.Rules)
        {
            if (rule.Enabled && string.Equals(rule.Source.Id, sourceId, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(rule);
            }
        }

        return matches;
    }

    private void WarnAboutDuplicateSources()
    {
        var duplicates = _config.Rules
            .Where(rule => rule.Enabled && !string.IsNullOrWhiteSpace(rule.Source.Id))
            .GroupBy(rule => rule.Source.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        // Every matching rule is applied, in order, so two rules on one source with different
        // targets means the second wins and the first looks broken. Warned about once per read
        // rather than once per report, which would be one line per event forever.
        foreach (var sourceId in duplicates)
        {
            _logger.LogWarning(
                "More than one enabled rule watches {SourceId}. All of them are applied in order, so the last one decides which device ends up default.",
                sourceId);
        }
    }

    private void Apply(Rule rule, Action<Rule> action)
    {
        try
        {
            action(rule);
        }
        catch (Exception ex)
        {
            // One rule's failure must not cost the others theirs, and must not end the consumer.
            _logger.LogError(ex, "Applying rule {RuleName} failed.", rule.Name);
        }
    }
}
