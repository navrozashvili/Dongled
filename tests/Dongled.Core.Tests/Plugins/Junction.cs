using System.Diagnostics;
using System.IO;

namespace Dongled.Core.Tests.Plugins;

/// <summary>
/// Creates a directory junction, which is the link a plugin directory is most likely to be in
/// practice. Unlike a symbolic link, a junction needs no elevation and no Developer Mode, so this
/// usually succeeds where <see cref="File.CreateSymbolicLink"/> does not.
/// </summary>
internal static class Junction
{
    public static bool TryCreate(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo(
            "cmd.exe",
            $"/c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        if (process is null)
        {
            return false;
        }

        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}
