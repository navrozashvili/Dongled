using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Dongled.App.Startup;

/// <summary>Which executable the start-with-Windows entry launches, relative to this one.</summary>
internal enum StartupTarget
{
    /// <summary>There is no entry, so nothing starts at logon.</summary>
    None = 0,

    /// <summary>The entry launches this executable.</summary>
    ThisCopy = 1,

    /// <summary>The entry launches an executable that no longer exists, so nothing starts at logon.</summary>
    MissingCopy = 2,

    /// <summary>The entry launches a different copy of the app that still exists.</summary>
    OtherCopy = 3,
}

/// <summary>What the start-with-Windows entry currently says.</summary>
/// <param name="Target">How the recorded executable relates to this one.</param>
/// <param name="RecordedPath">The executable the entry launches, unquoted, or null when there is none.</param>
internal sealed record StartupEntry(StartupTarget Target, string? RecordedPath)
{
    /// <summary>No entry.</summary>
    public static StartupEntry None { get; } = new(StartupTarget.None, null);

    /// <summary>Work out what a recorded Run value means for the executable asking.</summary>
    /// <param name="recorded">The raw Run value, possibly quoted, or null.</param>
    /// <param name="thisExecutable">The full path of the running executable.</param>
    /// <param name="exists">Whether a file exists, injected so this can be tested without a disk.</param>
    public static StartupEntry Classify(string? recorded, string thisExecutable, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(thisExecutable);
        ArgumentNullException.ThrowIfNull(exists);

        if (string.IsNullOrWhiteSpace(recorded))
        {
            return None;
        }

        var path = Unquote(recorded.Trim());

        if (string.Equals(path, thisExecutable, StringComparison.OrdinalIgnoreCase))
        {
            return new StartupEntry(StartupTarget.ThisCopy, path);
        }

        return new StartupEntry(exists(path) ? StartupTarget.OtherCopy : StartupTarget.MissingCopy, path);
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;
}

/// <summary>Start-with-Windows, per user only.</summary>
internal interface IStartupRegistration
{
    /// <summary>
    /// What the entry currently launches. A registry that cannot be read reads as no entry rather
    /// than throwing, because the Settings page must still open.
    /// </summary>
    StartupEntry Read();

    /// <summary>Point the entry at this executable, or remove it.</summary>
    /// <param name="enabled">Whether the app should start at logon.</param>
    /// <exception cref="Exception">
    /// The registry could not be written. Surfaced rather than swallowed, because a toggle that
    /// silently fails to stick is worse than one that says why.
    /// </exception>
    void SetEnabled(bool enabled);
}

/// <summary>
/// Start-with-Windows as one value under <c>HKCU\...\Run</c>.
/// </summary>
/// <remarks>
/// There is deliberately no machine-wide option. It would mean writing <c>HKLM\...\Run</c>, which
/// needs an elevated process, some way of telling that process what to write, and a registry value
/// every user on the machine executes: three pieces of attack surface in exchange for a checkbox.
/// No code path in the app requests administrator, and there is no switch that makes it write
/// anywhere else.
/// </remarks>
internal sealed class StartupRegistration : IStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The value name under the Run key. Also what the user would look for in Task Manager.</summary>
    private const string ValueName = "Dongled";

    private readonly string _executablePath;

    /// <param name="executablePath">
    /// The apphost to launch at logon. Starting the managed dll directly would not bring the Windows
    /// App SDK bootstrapper with it.
    /// </param>
    public StartupRegistration(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
    }

    /// <summary>The running process's own executable.</summary>
    public static StartupRegistration ForThisProcess() =>
        new(Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0]);

    /// <inheritdoc />
    public StartupEntry Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return StartupEntry.Classify(key?.GetValue(ValueName) as string, _executablePath, File.Exists);
        }
        catch (Exception)
        {
            return StartupEntry.None;
        }
    }

    /// <inheritdoc />
    public void SetEnabled(bool enabled)
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
                string.Create(CultureInfo.InvariantCulture, $"\"{_executablePath}\""),
                RegistryValueKind.String);

            return;
        }

        key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>Keeps start-with-Windows pointing at a copy that exists.</summary>
internal static class StartupEntryRepair
{
    /// <summary>
    /// Re-point an entry whose executable is gone at this one, for an official build only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A missing target means the user asked to start at logon and then moved or deleted the copy
    /// that was registered, most often by unzipping a newer release somewhere else. Nothing starts
    /// at logon in that state, so taking the entry over restores what they asked for.
    /// </para>
    /// <para>
    /// An entry naming another copy that still exists is left alone: someone running a build from
    /// source beside a release decides which one starts, from the Settings page. A build from source
    /// never repairs, so trying one out cannot quietly take over a release's startup.
    /// </para>
    /// </remarks>
    /// <returns>Whether the entry was re-pointed.</returns>
    public static bool Apply(IStartupRegistration registration, bool isOfficialBuild, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(logger);

        if (!isOfficialBuild)
        {
            return false;
        }

        var entry = registration.Read();
        if (entry.Target != StartupTarget.MissingCopy)
        {
            return false;
        }

        try
        {
            registration.SetEnabled(true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Start with Windows points at {Path}, which no longer exists, and could not be moved to this copy.", entry.RecordedPath);
            return false;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Start with Windows pointed at {Path}, which no longer exists; it now starts this copy.", entry.RecordedPath);
        }

        return true;
    }
}
