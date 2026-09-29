using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Dongled.App.Presentation;
using Dongled.Core.Audio;
using Dongled.Core.Configuration;
using Dongled.Core.Pipeline;

namespace Dongled.App.ViewModels;

/// <summary>One rule in the list beside the editor.</summary>
/// <remarks>
/// Mutable and observable so the list can be updated in place. Replacing items would make the
/// <c>ListView</c> rebuild its rows and reassign its selection, which takes focus away from
/// whatever the user is typing in the editor.
/// </remarks>
internal sealed partial class RuleListItem : ObservableObject
{
    /// <param name="id">The rule's stable identifier.</param>
    /// <param name="name">What the user called it, or a stand-in.</param>
    /// <param name="summary">A one-line description, in names rather than identifiers.</param>
    /// <param name="enabled">Whether it participates in switching.</param>
    public RuleListItem(string id, string name, string summary, bool enabled)
    {
        Id = id;
        Name = name;
        Summary = summary;
        Enabled = enabled;
    }

    /// <summary>The rule's stable identifier. Never displayed, and never changes.</summary>
    public string Id { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    public partial bool Enabled { get; set; }
}

/// <summary>Keeps the list of <see cref="RuleListItem"/> in step with configuration.</summary>
internal static class RuleList
{
    /// <summary>What a rule without a name is listed as.</summary>
    public const string UntitledName = "Untitled rule";

    /// <summary>Bring <paramref name="items"/> into line with <paramref name="rules"/>, reusing existing items.</summary>
    /// <param name="items">The list the page shows.</param>
    /// <param name="rules">The rules, in configuration order.</param>
    /// <param name="endpoints">The current devices, for naming targets in the summaries.</param>
    /// <param name="sources">The current sources, for naming them in the summaries.</param>
    /// <remarks>
    /// Items are matched by identifier and updated rather than replaced, so the item the
    /// <c>ListView</c> holds as its selection survives and focus stays where the user left it.
    /// </remarks>
    public static void Reconcile(
        ObservableCollection<RuleListItem> items,
        IReadOnlyList<Rule> rules,
        IReadOnlyList<AudioEndpoint> endpoints,
        IReadOnlyList<SourceState> sources)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(rules);

        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            var name = string.IsNullOrWhiteSpace(rule.Name) ? UntitledName : rule.Name;
            var summary = Summarize(rule, endpoints, sources);

            var existing = items.FirstOrDefault(item => string.Equals(item.Id, rule.Id, StringComparison.Ordinal));

            if (existing is null)
            {
                items.Insert(index, new RuleListItem(rule.Id, name, summary, rule.Enabled));
                continue;
            }

            var currentIndex = items.IndexOf(existing);
            if (currentIndex != index)
            {
                items.Move(currentIndex, index);
            }

            existing.Name = name;
            existing.Summary = summary;
            existing.Enabled = rule.Enabled;
        }

        while (items.Count > rules.Count)
        {
            items.RemoveAt(items.Count - 1);
        }
    }

    /// <summary>A one-line description of a rule, in names rather than identifiers.</summary>
    public static string Summarize(Rule rule, IReadOnlyList<AudioEndpoint> endpoints, IReadOnlyList<SourceState> sources)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var source = DisplayNames.ForSource(sources, rule.Source.Id, rule.Source.LastKnownName);
        var target = string.IsNullOrWhiteSpace(rule.Target.NamePattern)
            ? DisplayNames.ForDevice(endpoints, rule.Target.DeviceId, rule.Target.LastKnownName)
            : $"a device matching “{rule.Target.NamePattern}”";

        var summary = $"When {source} connects, make {target} the default";

        return rule.Enabled ? summary : summary + " (switched off)";
    }
}
