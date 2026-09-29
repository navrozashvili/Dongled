using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Dongled.App.Presentation;
using Dongled.Core.Audio;
using Dongled.Core.Configuration;
using Dongled.Core.Pipeline;

namespace Dongled.App.ViewModels;

/// <summary>
/// What the rules editor's three dropdowns offer: sources, target devices, and fallback devices.
/// </summary>
/// <remarks>
/// A rule can name a source or device that no longer exists. Leaving it out of the list would
/// silently repoint the rule at something else the moment anything is edited, so such an entry is
/// added back under the name configuration last recorded for it.
/// </remarks>
internal sealed class RuleChoiceLists
{
    /// <summary>The fallback list's first entry, which stands for "no fallback device".</summary>
    public static readonly ChoiceItem NoFallback = new(string.Empty, string.Empty, "Nothing — leave the default alone");

    /// <summary>What the "when this connects" dropdown offers.</summary>
    public ObservableCollection<ChoiceItem> Sources { get; } = [];

    /// <summary>What the "make this the default" dropdown offers.</summary>
    public ObservableCollection<ChoiceItem> Devices { get; } = [];

    /// <summary>
    /// What the fallback dropdown offers: the devices, preceded by an explicit "nothing" so that no
    /// fallback is something the user chose and can see they chose.
    /// </summary>
    public ObservableCollection<ChoiceItem> Fallbacks { get; } = [];

    /// <summary>The devices the lists were last built from.</summary>
    public IReadOnlyList<AudioEndpoint> Endpoints { get; private set; } = [];

    /// <summary>The sources the lists were last built from.</summary>
    public IReadOnlyList<SourceState> SourceStates { get; private set; } = [];

    /// <summary>Rebuild all three lists.</summary>
    /// <param name="endpoints">The current devices.</param>
    /// <param name="sources">The current sources.</param>
    /// <param name="config">Configuration, for the last-known names of anything absent.</param>
    /// <param name="keep">Identifiers that must stay in the lists even if they no longer exist.</param>
    public void Rebuild(
        IReadOnlyList<AudioEndpoint> endpoints,
        IReadOnlyList<SourceState> sources,
        AppConfig config,
        RuleChoiceIds keep)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(sources);

        Endpoints = endpoints;
        SourceStates = sources;

        Sources.Clear();
        foreach (var state in sources)
        {
            Sources.Add(new ChoiceItem(state.Descriptor.SourceId, state.Descriptor.DisplayName, state.Descriptor.DisplayName));
        }

        Devices.Clear();
        Fallbacks.Clear();
        Fallbacks.Add(NoFallback);

        foreach (var endpoint in endpoints)
        {
            var label = endpoint.IsActive
                ? endpoint.DisplayName
                : $"{endpoint.DisplayName} — not currently connected";

            Devices.Add(new ChoiceItem(endpoint.Id, endpoint.DisplayName, label));
            Fallbacks.Add(new ChoiceItem(endpoint.Id, endpoint.DisplayName, label));
        }

        EnsurePresent(keep, config);
    }

    /// <summary>Add entries for any of <paramref name="ids"/> the lists do not already offer.</summary>
    /// <param name="ids">The identifiers a rule names.</param>
    /// <param name="config">Configuration, for the names to show for absent entries.</param>
    public void EnsurePresent(RuleChoiceIds ids, AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!string.IsNullOrWhiteSpace(ids.Source) && Find(Sources, ids.Source) is null)
        {
            Sources.Add(SourceChoiceFor(ids.Source, config));
        }

        if (!string.IsNullOrWhiteSpace(ids.Target) && Find(Devices, ids.Target) is null)
        {
            Devices.Add(DeviceChoiceFor(ids.Target, config));
        }

        if (!string.IsNullOrWhiteSpace(ids.Fallback) && Find(Fallbacks, ids.Fallback) is null)
        {
            Fallbacks.Add(DeviceChoiceFor(ids.Fallback, config));
        }
    }

    public ChoiceItem? FindSource(string? id) => Find(Sources, id);

    public ChoiceItem? FindDevice(string? id) => Find(Devices, id);

    /// <summary>The fallback entry for <paramref name="id"/>, or <see cref="NoFallback"/>.</summary>
    public ChoiceItem FindFallback(string? id) => Find(Fallbacks, id) ?? NoFallback;

    private static ChoiceItem? Find(IEnumerable<ChoiceItem> choices, string? id) =>
        id is null ? null : choices.FirstOrDefault(choice => string.Equals(choice.Id, id, StringComparison.OrdinalIgnoreCase));

    private ChoiceItem SourceChoiceFor(string sourceId, AppConfig config)
    {
        var lastKnown = LastKnownSourceName(sourceId, config);

        var published = SourceStates.FirstOrDefault(
            state => string.Equals(state.Descriptor.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));

        var name = published is not null && !string.IsNullOrWhiteSpace(published.Descriptor.DisplayName)
            ? published.Descriptor.DisplayName
            : lastKnown ?? DisplayNames.UnnameableSource;

        return new ChoiceItem(sourceId, name, DisplayNames.ForSource(SourceStates, sourceId, lastKnown));
    }

    private ChoiceItem DeviceChoiceFor(string deviceId, AppConfig config)
    {
        var lastKnown = LastKnownDeviceName(deviceId, config);

        var live = Endpoints.FirstOrDefault(
            endpoint => string.Equals(endpoint.Id, deviceId, StringComparison.OrdinalIgnoreCase));

        // The bare name, never the decorated label, because this is what gets stored.
        var name = live is not null && !string.IsNullOrWhiteSpace(live.DisplayName)
            ? live.DisplayName
            : lastKnown ?? DisplayNames.UnnameableDevice;

        return new ChoiceItem(deviceId, name, DisplayNames.ForDevice(Endpoints, deviceId, lastKnown));
    }

    private static string? LastKnownSourceName(string sourceId, AppConfig config) => config.Rules
        .Where(rule => string.Equals(rule.Source.Id, sourceId, StringComparison.OrdinalIgnoreCase))
        .Select(static rule => rule.Source.LastKnownName)
        .FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name));

    private static string? LastKnownDeviceName(string deviceId, AppConfig config) => config.Rules
        .SelectMany(static rule => new[]
        {
            (Id: rule.Target.DeviceId, Name: rule.Target.LastKnownName),
            (Id: rule.OnDisconnect.FallbackDeviceId ?? string.Empty, Name: rule.OnDisconnect.FallbackLastKnownName),
        })
        .Where(pair => string.Equals(pair.Id, deviceId, StringComparison.OrdinalIgnoreCase))
        .Select(static pair => pair.Name)
        .FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name));
}

/// <summary>The source, target and fallback identifiers a rule refers to.</summary>
internal readonly record struct RuleChoiceIds(string? Source, string? Target, string? Fallback)
{
    /// <summary>The identifiers <paramref name="rule"/> names.</summary>
    public static RuleChoiceIds Of(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return new(rule.Source.Id, rule.Target.DeviceId, rule.OnDisconnect.FallbackDeviceId);
    }
}
