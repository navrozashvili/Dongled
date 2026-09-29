using Dongled.Abstractions;

namespace Dongled.Plugin.HyperXHid;

/// <summary>
/// Turns the dongle's charging edges into a charge state this plugin is willing to stand behind.
/// </summary>
/// <remarks>
/// <para>
/// The dongle announces charging only when it changes. Nothing in the periodic traffic carries a
/// flag, so an app that starts with the cable already in never hears anything and cannot know. That
/// is a limitation of the hardware, and this type exists to keep the honest answer — Unknown —
/// rather than guessing.
/// </para>
/// <para>
/// Charging latches: once an edge says charging began, that stays true until an edge says otherwise,
/// because no repeat is coming. A stop edge does the opposite and ages out, because after long
/// enough the cable may have gone back in while nothing was listening.
/// </para>
/// <para>
/// Pure and clock-injected, so every branch is testable without hardware. The rule comes from
/// HyperXBatteryHID, where it has been in daily use.
/// </para>
/// </remarks>
internal sealed class HyperXChargingTracker
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _staleAfter;

    private bool? _lastEdge;
    private DateTimeOffset _lastEdgeAt;

    /// <param name="time">Clock, injected so the staleness rule is testable.</param>
    /// <param name="staleAfter">How long a "charging stopped" edge stays believable.</param>
    public HyperXChargingTracker(TimeProvider time, TimeSpan staleAfter)
    {
        ArgumentNullException.ThrowIfNull(time);

        _time = time;
        _staleAfter = staleAfter;
    }

    /// <summary>Record an edge the dongle reported.</summary>
    /// <param name="charging">True if charging started, false if it stopped.</param>
    public void Observe(bool charging)
    {
        _lastEdge = charging;
        _lastEdgeAt = _time.GetUtcNow();
    }

    /// <summary>Forget everything. Called when the session ends.</summary>
    /// <remarks>
    /// What was true of the headset that just went away says nothing about the next one to pair with
    /// the dongle.
    /// </remarks>
    public void Reset() => _lastEdge = null;

    /// <summary>What this plugin is prepared to claim right now.</summary>
    public ChargeState Current()
    {
        if (_lastEdge is not { } charging)
        {
            return ChargeState.Unknown;
        }

        if (charging)
        {
            // Latched. No repeat is coming, so ageing this out would flip a genuinely charging
            // device to Unknown a few seconds after it was plugged in.
            return ChargeState.Charging;
        }

        return _time.GetUtcNow() - _lastEdgeAt > _staleAfter
            ? ChargeState.Unknown
            : ChargeState.Discharging;
    }
}
