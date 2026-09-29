using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Dongled.App.Presentation;
using Dongled.Core.Audio;

namespace Dongled.App.ViewModels;

/// <summary>
/// The "Match by name" part of a device choice: whether the device is found by a pattern on its
/// name, the pattern itself, and a line saying what it matches right now.
/// </summary>
/// <remarks>
/// Matching by name keeps a rule working when Windows renumbers a device. The device picked in the
/// dropdown fills the pattern in and is preferred when several devices match.
/// </remarks>
internal sealed class NamePatternField : ObservableObject
{
    private readonly Func<ChoiceItem?> _pickedDevice;
    private readonly Func<IReadOnlyList<AudioEndpoint>> _endpoints;
    private readonly Action _toggled;
    private readonly Action _typed;

    private bool _usesPattern;
    private string _pattern = string.Empty;
    private PatternStatus? _status;

    /// <param name="pickedDevice">The device currently picked in the matching dropdown.</param>
    /// <param name="endpoints">The devices the pattern is tested against.</param>
    /// <param name="toggled">Called when the user switches matching on or off.</param>
    /// <param name="typed">Called when the user edits the pattern.</param>
    public NamePatternField(
        Func<ChoiceItem?> pickedDevice,
        Func<IReadOnlyList<AudioEndpoint>> endpoints,
        Action toggled,
        Action typed)
    {
        ArgumentNullException.ThrowIfNull(pickedDevice);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(toggled);
        ArgumentNullException.ThrowIfNull(typed);

        _pickedDevice = pickedDevice;
        _endpoints = endpoints;
        _toggled = toggled;
        _typed = typed;
    }

    /// <summary>Whether the device is chosen by a pattern on its name rather than by the dropdown alone.</summary>
    public bool UsesPattern
    {
        get => _usesPattern;
        set
        {
            if (!SetProperty(ref _usesPattern, value))
            {
                return;
            }

            // Switching on starts from the device already picked, so the common case — "this
            // device, however Windows numbers it" — is a single tick.
            if (value && string.IsNullOrWhiteSpace(_pattern) && PatternFrom(_pickedDevice()) is { } pattern)
            {
                SetProperty(ref _pattern, pattern, nameof(Pattern));
            }

            RefreshStatus();
            _toggled();
        }
    }

    /// <summary>The pattern, a case-insensitive regular expression.</summary>
    public string Pattern
    {
        get => _pattern;
        set
        {
            if (!SetProperty(ref _pattern, value ?? string.Empty))
            {
                return;
            }

            RefreshStatus();
            _typed();
        }
    }

    /// <summary>What the pattern matches right now, or empty when matching is off.</summary>
    public string Status => _status?.Text ?? string.Empty;

    /// <summary>Show a stored pattern without treating it as an edit.</summary>
    /// <param name="pattern">The stored pattern, or null for none.</param>
    public void Load(string? pattern)
    {
        SetProperty(ref _usesPattern, !string.IsNullOrWhiteSpace(pattern), nameof(UsesPattern));
        SetProperty(ref _pattern, pattern ?? string.Empty, nameof(Pattern));
    }

    /// <summary>Fill the pattern in from a device the user just picked, if matching is on.</summary>
    public void FillFrom(ChoiceItem? picked)
    {
        if (_usesPattern && PatternFrom(picked) is { } pattern)
        {
            SetProperty(ref _pattern, pattern, nameof(Pattern));
        }
    }

    /// <summary>Recompute <see cref="Status"/> against the current devices and pick.</summary>
    public void RefreshStatus()
    {
        _status = _usesPattern
            ? PatternStatus.For(_pattern, _pickedDevice()?.Id, _endpoints())
            : null;

        OnPropertyChanged(nameof(Status));
    }

    /// <summary>The pattern to store, given the one stored now.</summary>
    /// <param name="stored">What configuration holds at the moment.</param>
    /// <returns>
    /// Null when matching is off. Otherwise the typed pattern if it is usable, or
    /// <paramref name="stored"/> if it is not: the status line says what is wrong, and the rule
    /// keeps its last usable pattern rather than matching nothing.
    /// </returns>
    public string? PatternToStore(string? stored)
    {
        if (!_usesPattern)
        {
            return null;
        }

        return !string.IsNullOrWhiteSpace(_pattern) && DeviceNameMatcher.TryValidate(_pattern, out _)
            ? _pattern
            : stored;
    }

    /// <summary>A literal pattern for a picked device, or null for no pick or the explicit "nothing".</summary>
    private static string? PatternFrom(ChoiceItem? choice) =>
        choice is null || choice.Id.Length == 0 || string.IsNullOrWhiteSpace(choice.Name)
            ? null
            : DeviceNameMatcher.PatternFor(choice.Name);
}
