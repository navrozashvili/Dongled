using System.Globalization;
using Microsoft.Win32;

namespace Dongled.App.Startup;

/// <summary>
/// Start-with-Windows, per user only.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no machine-wide option. It would mean writing <c>HKLM\...\Run</c>, which
/// needs an elevated process, some way of telling that process what to write, and a registry value
/// every user on the machine executes: three pieces of attack surface in exchange for a checkbox.
/// </para>
/// <para>
/// So this writes one value under <c>HKCU</c> and nothing else. No code path in the app requests
/// administrator, and there is no switch that makes it write anywhere else.
/// </para>
/// </remarks>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The value name under the Run key. Also what the user would look for in Task Manager.</summary>
    private const string ValueName = "Dongled";

    /// <summary>
    /// Whether the Run value exists and points at this executable.
    /// </summary>
    /// <remarks>
    /// A value naming a <em>different</em> executable reads as off, deliberately: it is almost
    /// always a stale entry from a copy that used to live elsewhere, and reporting it as on would
    /// leave the user with a checkbox that is ticked while nothing they can see starts.
    /// </remarks>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var recorded = key?.GetValue(ValueName) as string;

            return !string.IsNullOrWhiteSpace(recorded)
                && string.Equals(Unquote(recorded), ExecutablePath(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // A registry that cannot be read is reported as "not registered" rather than throwing.
            // The Settings page shows a checkbox, and an exception there would be a crash on a page
            // the user opened to look at a version number.
            return false;
        }
    }

    /// <summary>Add or remove the Run value.</summary>
    /// <param name="enabled">Whether the app should start at logon.</param>
    /// <exception cref="Exception">
    /// The registry could not be written. Surfaced rather than swallowed, because a checkbox that
    /// silently fails to stick is worse than one that says why.
    /// </exception>
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException(
                $@"Could not open HKCU\{RunKeyPath} for writing, so start-with-Windows cannot be changed.");

        if (enabled)
        {
            // Quoted because the path contains spaces on any normal installation, and an unquoted
            // Run value is parsed at the first space.
            key.SetValue(
                ValueName,
                string.Create(CultureInfo.InvariantCulture, $"\"{ExecutablePath()}\""),
                RegistryValueKind.String);

            return;
        }

        key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string ExecutablePath() =>
        // ProcessPath is the apphost, which is what has to be launched: starting the managed dll
        // directly would not bring the Windows App SDK bootstrapper with it.
        Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;
}
