using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Dongled.Core.Configuration;

/// <summary>Stores user configuration in a single JSON file.</summary>
public sealed class ConfigStore : IConfigStore
{
    /// <summary>
    /// Upper bound on the stabilization delay. Less a user-facing limit than a guard against a
    /// hand-edited file parking the app where a disconnect appears to do nothing for an hour.
    /// Five minutes is the ceiling because it already exceeds any plausible flicker or
    /// reconnect cycle, so nothing a real device does needs longer, and a user who waited that
    /// long would read the silence as the app being broken rather than as a delay.
    /// </summary>
    private const int MaximumStabilizationSeconds = 300;

    /// <summary>
    /// Highest schema version this build understands. A file declaring a higher one is treated
    /// as unreadable rather than guessed at.
    /// </summary>
    private const int CurrentSchemaVersion = 1;

    private readonly string _filePath;
    private readonly ILogger<ConfigStore> _logger;

    /// <param name="filePath">Full path to the configuration file.</param>
    /// <param name="logger">Where to report an unreadable file.</param>
    public ConfigStore(string filePath, ILogger<ConfigStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(logger);

        _filePath = filePath;
        _logger = logger;
    }

    /// <inheritdoc />
    public AppConfig Load()
    {
        string? json;
        try
        {
            json = AtomicFile.ReadAllTextOrNull(_filePath);
        }
        catch (Exception ex)
        {
            // Deliberately broad: no failure to read this file may stop the app starting.
            _logger.LogWarning(ex, "Could not read the configuration file at {Path}. Using defaults.", _filePath);
            return new AppConfig();
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return new AppConfig();
        }

        AppConfig? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AppConfig>(json, JsonSetup.Options);
        }
        catch (Exception ex)
        {
            // Deliberately broad, like the read above: no deserializer failure of any kind may
            // stop the app starting. Bad JSON raises JsonException, but the serializer can fail
            // for reasons that have nothing to do with the file - enabling PublishAot or
            // PublishTrimmed makes every reflection-based Deserialize call throw
            // InvalidOperationException - and a build property must not be able to make the app
            // unlaunchable. Anything that goes wrong here degrades to defaults instead.
            _logger.LogWarning(
                ex,
                "The configuration file at {Path} could not be deserialized. Using defaults; the file is left in place so it can be inspected.",
                _filePath);
            return new AppConfig();
        }

        if (parsed is null)
        {
            _logger.LogWarning("The configuration file at {Path} contained no object. Using defaults.", _filePath);
            return new AppConfig();
        }

        // A file from a newer version may use fields or meanings this build does not know,
        // so guessing at it risks acting on a misread rule. Fall back to defaults and leave
        // the file alone, so downgrading and re-upgrading does not destroy a newer config.
        // This is the behaviour AppConfig.SchemaVersion's doc comment promises.
        if (parsed.SchemaVersion > CurrentSchemaVersion)
        {
            _logger.LogWarning(
                "The configuration file at {Path} declares schema version {FileVersion}, but this build understands only {SupportedVersion}. Using defaults; the file is left unchanged.",
                _filePath,
                parsed.SchemaVersion,
                CurrentSchemaVersion);
            return new AppConfig();
        }

        return Normalize(parsed);
    }

    /// <inheritdoc />
    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Load leaves a newer file alone, but returns defaults, so the user sees an unconfigured
        // app. The first save of anything at all, a theme toggle included, would otherwise write
        // this build's shape over a configuration the user never saw and did not know was there.
        SchemaGuard.PreserveNewerFile(_filePath, CurrentSchemaVersion, _logger);

        // No try/catch here on purpose. Both exceptions AtomicFile.WriteAllText can raise reach
        // the caller, including UnauthorizedAccessException, which does not derive from
        // IOException and would escape a catch narrowed to that.
        AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(config, JsonSetup.Options));
    }

    /// <summary>
    /// Repair what a hand-edited or truncated file can express but the rest of the app assumes
    /// away: missing objects, null collections, null list entries, out-of-range numbers, and
    /// enum values with no declared name.
    /// </summary>
    private AppConfig Normalize(AppConfig config)
    {
        config.App ??= new AppSettings();
        config.Rules ??= [];
        config.Plugins ??= [];
        config.App.BatteryDisplayOrder ??= [];

        // A file can say "rules": [null], and the serializer faithfully materializes that as a
        // null inside a list every later consumer types as non-nullable; without this the loop
        // below dereferences it and Load throws, which is the one thing it may never do.
        // Dropping the entry is the only sound repair: turning it into a default Rule would
        // invent an enabled rule with an empty source for the engine to evaluate.
        config.Rules.RemoveAll(rule => rule is null);
        config.Plugins.RemoveAll(plugin => plugin is null);

        // Same hazard, one layer down: "batteryDisplayOrder": [null, "src:x"] deserializes with a
        // null entry a later consumer types as non-nullable string.
        config.App.BatteryDisplayOrder.RemoveAll(id => id is null);

        var requested = config.App.DisconnectStabilizationSeconds;
        var clamped = Math.Clamp(requested, 0, MaximumStabilizationSeconds);
        if (clamped != requested)
        {
            _logger.LogWarning(
                "Stabilization delay of {Requested}s is outside the supported range and was clamped to {Clamped}s.",
                requested,
                clamped);
            config.App.DisconnectStabilizationSeconds = clamped;
        }

        // Enums need the same treatment the number above gets, for the same reason and one more.
        // JsonStringEnumConverter reads names but also accepts numbers, so "theme": 9 loads as
        // (AppTheme)9 - a value no switch, no binding and no comparison has a branch for. The
        // extra reason is the write side: the next save serializes it back out as a bare 9,
        // putting a number into a file whose whole enum contract is names, and a member added at
        // that value later would silently give the file a meaning nobody chose. Rejecting numbers
        // in the converter instead would be strictly worse: that raises JsonException, which the
        // broad catch above turns into the whole configuration being replaced by defaults, so one
        // mistyped field would cost every rule in the file.
        if (!Enum.IsDefined(config.App.Theme))
        {
            _logger.LogWarning(
                "Theme value {Theme} is not one this build defines and was reset to {Default}.",
                (int)config.App.Theme,
                AppTheme.System);
            config.App.Theme = AppTheme.System;
        }

        foreach (var rule in config.Rules)
        {
            rule.Source ??= new RuleSource();
            rule.Target ??= new RuleTarget();
            rule.OnDisconnect ??= new DisconnectBehavior();
            rule.Target.Roles ??= [AudioRole.Media, AudioRole.Calls];

            // The same hazard one layer down. System.Text.Json ignores nullable reference
            // annotations, so an explicit "id": null overwrites the property initializer and
            // leaves a null in a field the type system says can never hold one. Load survives
            // because it never dereferences these, but a consumer matching on Source.Id or
            // keying a dictionary by it would throw a long way from the file that caused it.
            rule.Id ??= string.Empty;
            rule.Name ??= string.Empty;
            rule.Source.Id ??= string.Empty;
            rule.Target.DeviceId ??= string.Empty;

            // Undefined roles are dropped rather than replaced. Which roles a rule switches is a
            // choice the user made, and substituting a different one would switch a device they
            // never asked about; a rule left with no roles switches nothing, which is visible in
            // the UI and fixable there. That includes dropping every role, so the list can come
            // back empty - the alternative, reinstating both defaults, would turn a file naming
            // one unreadable role into a rule that grabs the communications endpoint too.
            var droppedRoles = rule.Target.Roles.RemoveAll(role => !Enum.IsDefined(role));
            if (droppedRoles > 0)
            {
                _logger.LogWarning(
                    "Rule {RuleId} named {DroppedCount} audio role(s) this build does not define; they were dropped.",
                    rule.Id,
                    droppedRoles);
            }

            // The disconnect mode is reset rather than dropped: unlike a role it is not optional,
            // and the default is the least surprising of the four.
            if (!Enum.IsDefined(rule.OnDisconnect.Mode))
            {
                _logger.LogWarning(
                    "Rule {RuleId} names disconnect mode {Mode}, which this build does not define; it was reset to {Default}.",
                    rule.Id,
                    (int)rule.OnDisconnect.Mode,
                    DisconnectMode.RestorePreviousElseFallback);
                rule.OnDisconnect.Mode = DisconnectMode.RestorePreviousElseFallback;
            }
        }

        foreach (var plugin in config.Plugins)
        {
            plugin.Directory ??= string.Empty;

            if (!Enum.IsDefined(plugin.LogLevel))
            {
                _logger.LogWarning(
                    "Plugin {Directory} names log level {LogLevel}, which this build does not define; it was reset to {Default}.",
                    plugin.Directory,
                    (int)plugin.LogLevel,
                    LogLevel.Warning);
                plugin.LogLevel = LogLevel.Warning;
            }
        }

        return config;
    }
}
