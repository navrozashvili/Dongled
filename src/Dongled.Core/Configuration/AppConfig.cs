namespace Dongled.Core.Configuration;

/// <summary>Which colour scheme the window uses.</summary>
/// <remarks>
/// The numeric values are persisted in configuration and must not be renumbered or reordered;
/// new members go on the end.
/// </remarks>
public enum AppTheme
{
    /// <summary>Follow the Windows setting.</summary>
    System = 0,

    /// <summary>Always light.</summary>
    Light = 1,

    /// <summary>Always dark.</summary>
    Dark = 2,
}

/// <summary>Application-wide settings.</summary>
public sealed class AppSettings
{
    /// <summary>Whether to start when the current user logs in. Per user only; never machine wide.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Whether to start with no window, showing only the notification-area icon.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applies to every launch, not only the one Windows performs at logon. Telling the two apart
    /// would mean putting a switch on the command line the Run value writes, and the app
    /// deliberately has no command-line handling; a setting is the same outcome without an
    /// argument that decides what the process does.
    /// </para>
    /// <para>
    /// Nothing is lost by starting hidden, because the app does its work whether or not a window is
    /// open: the switching engine and every provider run regardless, and clicking the tray icon
    /// brings the window up. Launching the executable a second time does the same, so a shortcut
    /// still opens the window.
    /// </para>
    /// </remarks>
    public bool StartMinimized { get; set; }

    /// <summary>Whether to reconcile rules against current device state at startup.</summary>
    public bool ApplyRulesOnStartup { get; set; } = true;

    /// <summary>
    /// How long to wait after a source disconnects before applying its return behaviour, so a
    /// device that flickers does not cause a switch. Applies to every rule.
    /// </summary>
    public int DisconnectStabilizationSeconds { get; set; } = 5;

    /// <summary>
    /// Whether the window follows the Windows light or dark setting, or is pinned to one of them
    /// regardless of what Windows is set to.
    /// </summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>
    /// Source identifiers in the order the user wants battery levels considered, most preferred
    /// first. The notification-area icon shows the first entry actually reporting a level; anything
    /// not listed here ranks after everything that is.
    /// </summary>
    /// <remarks>
    /// Identifiers rather than indices, because the set of sources changes as plugins are enabled
    /// and an index would come to mean a different device. An identifier no plugin publishes any
    /// more is kept rather than pruned: the user may be about to plug that device back in, and
    /// forgetting their ordering because a dongle was unplugged would be a defect.
    /// </remarks>
    public List<string> BatteryDisplayOrder { get; set; } = [];

    /// <summary>Whether to ask GitHub for a newer release when the user opens the window.</summary>
    /// <remarks>
    /// <para>
    /// On unless the user turns it off. A file written before this setting existed has no value
    /// for it, and the serializer leaves the initializer's <see langword="true"/> in place, so the
    /// schema version did not need to change.
    /// </para>
    /// <para>
    /// Only official builds ever check; in a build from source this is ignored and not shown.
    /// Checking now from the Settings page works whether this is on or off, because that is the
    /// user asking.
    /// </para>
    /// </remarks>
    public bool CheckForUpdates { get; set; } = true;
}

/// <summary>
/// Everything the user configures. Written only by the UI.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Schema version of this file. Bumped only for a breaking change.</summary>
    /// <remarks>
    /// A file whose <c>schemaVersion</c> is higher than the running app understands is treated as
    /// unreadable: the app falls back to defaults, logs that it did so, and leaves the file on
    /// disk untouched rather than overwriting it. Because the user is then looking at defaults,
    /// the first save would overwrite that file, so a save that is about to replace a
    /// newer-versioned file copies it to <c>config.json.newer-vN.bak</c> beside it first, where
    /// N is the version the file declared. Running an older build therefore does not silently
    /// destroy a newer configuration, whether it only reads or goes on to write. No migration
    /// code is shipped.
    /// </remarks>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Application-wide settings.</summary>
    public AppSettings App { get; set; } = new();

    /// <summary>Switching rules, in the order the user arranged them.</summary>
    public List<Rule> Rules { get; set; } = [];

    /// <summary>Trust and logging settings, one entry per approved plugin directory.</summary>
    public List<PluginConfig> Plugins { get; set; } = [];
}
