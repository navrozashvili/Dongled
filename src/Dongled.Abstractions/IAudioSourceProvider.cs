namespace Dongled.Abstractions;

/// <summary>
/// Watches for audio sources appearing and disappearing, and reports what it finds.
/// </summary>
/// <remarks>
/// Exactly one implementation per plugin assembly, declared with
/// <see cref="AudioSourceProviderAttribute"/>. The host constructs it through its public
/// parameterless constructor and calls <see cref="StartAsync"/> once.
/// </remarks>
public interface IAudioSourceProvider
{
    /// <summary>Identity of this provider. Read before <see cref="StartAsync"/>.</summary>
    ProviderMetadata Metadata { get; }

    /// <summary>
    /// Begin watching, and return once watching is established. Publish sources and report their
    /// presence as soon as each is known, rather than waiting until everything is resolved. The
    /// host applies a startup deadline, so a source still unreported when it expires is treated
    /// as absent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Do not run your watch loop inside this call. Start it, then return: the returned task
    /// completes when watching is established, not when the provider stops.
    /// </para>
    /// <para>
    /// The host gives this call a deadline of roughly ten seconds; the exact figure is the
    /// host's. A provider that has not returned by then is treated as failed, is given a
    /// <see cref="StopAsync"/> call, and is not started again until the plugin is re-enabled.
    /// </para>
    /// </remarks>
    /// <param name="context">Where to publish findings.</param>
    /// <param name="ct">
    /// Cancelled when the host is shutting down or the plugin is disabled. It stays valid after
    /// this method returns, so capture it for the watch loop rather than treating it as scoped to
    /// the call.
    /// </param>
    Task StartAsync(IProviderContext context, CancellationToken ct);

    /// <summary>
    /// Stop watching and release resources. Called once, and always called if
    /// <see cref="StartAsync"/> was called, including when it faulted.
    /// </summary>
    /// <param name="ct">Cancelled if shutdown is taking too long.</param>
    Task StopAsync(CancellationToken ct);
}
