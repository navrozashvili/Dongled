namespace Dongled.Abstractions;

/// <summary>
/// What the host hands a provider so it can publish what it found.
/// </summary>
/// <remarks>
/// <para>
/// Both methods are safe to call from any thread, including from inside
/// <see cref="IAudioSourceProvider.StartAsync"/> and <see cref="IAudioSourceProvider.StopAsync"/>.
/// Ordinarily a call returns immediately. If the host has fallen far behind, the call waits until
/// there is room for it, so a provider that floods them throttles itself rather than losing
/// reports. That is deliberate.
/// </para>
/// <para>
/// Because a call can wait, do not make one from a thread that must not be paused, such as a
/// driver or vendor SDK callback that has its own deadline. Hand the value to your own worker and
/// publish from there.
/// </para>
/// <para>Calls made by one provider take effect in the order that provider made them.</para>
/// </remarks>
public interface IProviderContext
{
    /// <summary>Logger scoped to this provider.</summary>
    IProviderLogger Logger { get; }

    /// <summary>
    /// Declare the complete set of sources this provider can report on. Call it again whenever
    /// the set changes; each call replaces the previous set for this provider, so the rules
    /// picker updates live as devices appear.
    /// </summary>
    /// <param name="sources">
    /// The complete set. <see cref="IReadOnlyList{T}"/> is a read-only view rather than a
    /// snapshot, and the host may read the list after this call returns, so pass a list you will
    /// not mutate afterwards and build a new one for each call.
    /// </param>
    void PublishSources(IReadOnlyList<AudioSourceDescriptor> sources);

    /// <summary>
    /// Report whether one source is currently present. Idempotent by construction: repeating the
    /// same presence is harmless and produces no duplicate switching, so providers need not track
    /// what they last reported. Report <see cref="Presence.Unknown"/> only when genuinely unable
    /// to tell, because at startup an unresolved source is ultimately treated as absent.
    /// </summary>
    void ReportPresence(string sourceId, Presence presence);

    /// <summary>
    /// Report a source's battery level, its charging state, or both. Optional: a provider that
    /// cannot see a battery simply never calls this.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first call for a source is also what declares that the source <em>has</em> a battery, so
    /// make one as soon as that is known — with <paramref name="percent"/> null and
    /// <paramref name="charge"/> <see cref="ChargeState.Unknown"/> if nothing else is known yet.
    /// Without it the host cannot distinguish "no battery" from "battery, currently switched off",
    /// and a device that is off would not be listed at all.
    /// </para>
    /// <para>
    /// Report what your device actually supports and no more. If it only announces charging on a
    /// transition, and you started after that transition, report
    /// <see cref="ChargeState.Unknown"/> rather than a guess — the host will not second-guess you,
    /// and a wrong charging indicator is worse than an absent one. Any latching, ageing, or
    /// smoothing your hardware needs belongs here in the provider, not in the host.
    /// </para>
    /// <para>Idempotent, like <see cref="ReportPresence"/>: repeating a value is harmless.</para>
    /// </remarks>
    /// <param name="sourceId">
    /// The source this is about. Must be one this provider publishes; a blank value is dropped.
    /// </param>
    /// <param name="percent">
    /// Charge remaining, 0 to 100 inclusive, or <see langword="null"/> when the level is not known.
    /// A value outside that range causes the whole report to be dropped and logged against this
    /// provider.
    /// </param>
    /// <param name="charge">Whether it is charging.</param>
    void ReportBattery(string sourceId, int? percent, ChargeState charge);
}
