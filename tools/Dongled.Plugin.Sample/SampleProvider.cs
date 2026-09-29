using System.Diagnostics.CodeAnalysis;
using Dongled.Abstractions;

// One attribute per plugin assembly. This is the whole of discovery: the host never scans types
// and never runs a constructor while working out what this assembly is.
[assembly: AudioSourceProvider(typeof(Dongled.Plugin.Sample.SampleProvider))]

namespace Dongled.Plugin.Sample;

/// <summary>
/// A provider that pretends to watch for one device, so the shape of a real one is visible without
/// any vendor SDK in the way.
/// </summary>
/// <remarks>
/// <para>
/// A real provider replaces <see cref="WatchAsync"/> with whatever its vendor offers: a HID device
/// watcher, a websocket to a local vendor service, a polling loop. Everything else stays as it is
/// here.
/// </para>
/// <para>
/// The two rules worth copying: <see cref="StartAsync"/> returns as soon as watching is
/// established rather than running the loop itself, and the token it is handed stays valid after
/// it returns, so it is captured rather than treated as scoped to the call.
/// </para>
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Constructed by the host through the assembly-level AudioSourceProvider attribute. The rule looks for an object-creation expression naming the type, which reflection never provides.")]
internal sealed class SampleProvider : IAudioSourceProvider
{
    private const string SourceId = "sample:demo-headset";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private static readonly AudioSourceDescriptor[] Sources =
    [
        new AudioSourceDescriptor(
            SourceId,
            "Sample demo headset",
            "not a real device; this plugin is the worked example"),
    ];

    private CancellationTokenSource? _stopping;
    private Task? _watching;

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new(
        Id: "sample.demo",
        DisplayName: "Sample provider",
        Description: "Publishes one imaginary source and reports it present. Worked example.",
        IsExperimental: true);

    /// <inheritdoc />
    public Task StartAsync(IProviderContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Linked, not the token itself, so StopAsync can stop the loop whether the host cancelled
        // or simply asked this plugin to stop.
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Publish before reporting presence: a presence report about a source the host has never
        // heard of is not useful to it.
        context.PublishSources(Sources);
        context.ReportPresence(SourceId, Presence.Present);

        if (context.Logger.IsEnabled(ProviderLogLevel.Information))
        {
            context.Logger.Log(ProviderLogLevel.Information, "Sample provider is watching.");
        }

        // Started, not awaited. Watching is established; returning the loop's task here would
        // hold the host until the deadline and then be treated as a failure to start.
        _watching = WatchAsync(context, _stopping.Token);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct)
    {
        if (_stopping is not null)
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }

        if (_watching is not null)
        {
            // Waiting here is what makes "released" true rather than merely requested. The host
            // calls this exactly once, and always if StartAsync was called.
            await _watching.ConfigureAwait(false);
            _watching = null;
        }

        _stopping?.Dispose();
        _stopping = null;
    }

    private static async Task WatchAsync(IProviderContext context, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);

                // Repeating a presence is free: the host is idempotent by construction, so a
                // provider never has to remember what it last reported.
                context.ReportPresence(SourceId, Presence.Present);
            }
        }
        catch (OperationCanceledException)
        {
            // The ordinary way this loop ends.
        }
    }
}
