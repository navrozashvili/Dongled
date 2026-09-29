using System.Diagnostics.CodeAnalysis;

// The configuration and state types are the shapes System.Text.Json serializes the two files
// into. CA2227 and CA1002 both fire on their collection properties, and neither fix is available
// here:
//
//   CA2227 wants the setters removed. System.Text.Json needs them: a getter-only property is
//   populated only when the deserializer can add to the existing instance, which does not hold
//   for a replaced collection, and the stores in the configuration layer round-trip whole
//   documents rather than mutating in place. Removing the setters would break loading.
//
//   CA1002 wants Collection<T> instead of List<T>. The rule guards against List<T> in a library
//   API surface that callers extend; these are data-transfer shapes whose only consumers are the
//   serializer and this application's own UI, and Collection<T> serializes no better while
//   costing an allocation and a less obvious type in the UI bindings.
//
// Init-only properties would satisfy CA2227 but block the whole-collection reassignment that the
// storage layer performs when it rebuilds a loaded document, and that the UI performs when it
// replaces a collection wholesale.
//
// Each suppression is scoped to a single property rather than to the type, the namespace, or the
// project, so both rules keep firing on any collection property added later that is not part of
// this serialized schema.

[assembly: SuppressMessage(
    "Usage",
    "CA2227:Collection properties should be read only",
    Scope = "member",
    Target = "~P:Dongled.Core.Configuration.RuleTarget.Roles",
    Justification = "Settable so System.Text.Json can assign the deserialized collection; this type is a serialized schema shape.")]
[assembly: SuppressMessage(
    "Usage",
    "CA2227:Collection properties should be read only",
    Scope = "member",
    Target = "~P:Dongled.Core.Configuration.AppConfig.Rules",
    Justification = "Settable so System.Text.Json can assign the deserialized collection; this type is a serialized schema shape.")]
[assembly: SuppressMessage(
    "Usage",
    "CA2227:Collection properties should be read only",
    Scope = "member",
    Target = "~P:Dongled.Core.Configuration.AppConfig.Plugins",
    Justification = "Settable so System.Text.Json can assign the deserialized collection; this type is a serialized schema shape.")]
[assembly: SuppressMessage(
    "Usage",
    "CA2227:Collection properties should be read only",
    Scope = "member",
    Target = "~P:Dongled.Core.Configuration.AppState.PreviousDefaults",
    Justification = "Settable so System.Text.Json can assign the deserialized collection; this type is a serialized schema shape.")]
[assembly: SuppressMessage(
    "Usage",
    "CA2227:Collection properties should be read only",
    Scope = "member",
    Target = "~P:Dongled.Core.Configuration.AppSettings.BatteryDisplayOrder",
    Justification = "Settable so System.Text.Json can assign the deserialized collection; this type is a serialized schema shape.")]

// CA1873 wants a logging call to sit behind an ILogger.IsEnabled check for the level being
// logged. ProviderLoggerAdapter.Log does exactly that, but through its own IsEnabled, which
// consults the plugin's configured minimum as well as the host logger and is the only check that
// can answer for a level that is not known until run time. The analyzer matches syntactically on
// a literal level, so it cannot see the guard, and there is no way to write this forwarding
// method that satisfies it. Scoped to the one method rather than the type, so the rule keeps
// firing on any unguarded logging added here later.
[assembly: SuppressMessage(
    "Performance",
    "CA1873:Avoid potentially expensive logging",
    Scope = "member",
    Target = "~M:Dongled.Core.Pipeline.ProviderLoggerAdapter.Log(Dongled.Abstractions.ProviderLogLevel,System.String,System.Exception)",
    Justification = "Guarded by this type's own IsEnabled, which the analyzer cannot match because the level is not a literal.")]

[assembly: SuppressMessage(
    "Design",
    "CA1002:Do not expose generic lists",
    Scope = "member",
    Target = "~P:Dongled.Core.Configuration.RuleTarget.Roles",
    Justification = "Data-transfer shape for System.Text.Json, not an extensible library API; Collection<T> would add cost without benefit.")]
[assembly: SuppressMessage(
    "Design",
    "CA1002:Do not expose generic lists",
    Scope = "member",
    Target = "~P:Dongled.Core.Configuration.AppConfig.Rules",
    Justification = "Data-transfer shape for System.Text.Json, not an extensible library API; Collection<T> would add cost without benefit.")]
[assembly: SuppressMessage(
    "Design",
    "CA1002:Do not expose generic lists",
    Scope = "member",
    Target = "~P:Dongled.Core.Configuration.AppConfig.Plugins",
    Justification = "Data-transfer shape for System.Text.Json, not an extensible library API; Collection<T> would add cost without benefit.")]
[assembly: SuppressMessage(
    "Design",
    "CA1002:Do not expose generic lists",
    Scope = "member",
    Target = "~P:Dongled.Core.Configuration.AppSettings.BatteryDisplayOrder",
    Justification = "Data-transfer shape for System.Text.Json, not an extensible library API; Collection<T> would add cost without benefit.")]
