using System.Diagnostics.CodeAnalysis;
using Dongled.App.Services;
using Dongled.Core.Plugins;

namespace Dongled.App.Tests.Fakes;

internal sealed class FakeInspection : IPackageInspection
{
    public PluginInstallOutcome Outcome { get; init; } = PluginInstallOutcome.Fresh;

    public string DirectoryName { get; init; } = "Sample";

    public string AssemblyName { get; init; } = "Dongled.Plugin.Sample";

    public IReadOnlyList<string> DifferingFiles { get; init; } = [];

    public bool IsInstalledPluginLoaded { get; init; }

    public string Message { get; init; } = "Sample is not installed yet.";

    public bool IsDisposed { get; private set; }

    public void Dispose() => IsDisposed = true;
}

internal sealed class FakePluginInstaller : IPluginInstaller
{
    /// <summary>What the next inspection returns; null means the file is not a plugin package.</summary>
    public FakeInspection? Inspection { get; set; }

    public string InspectionFailure { get; set; } = "The archive has no plugin assembly.";

    /// <summary>When set, inspecting throws it, as a zip malformed in an unexpected way does.</summary>
    public Exception? InspectionThrows { get; set; }

    /// <summary>What <see cref="Install"/> returns; by default a success with a fixed hash.</summary>
    public Func<IPackageInspection, PluginInstallResult> InstallResult { get; set; } =
        inspection => new PluginInstallResult(true, inspection.DirectoryName, "LANDED", $"{inspection.DirectoryName} was installed.");

    public Func<string, PluginInstallResult> RemoveResult { get; set; } =
        directory => new PluginInstallResult(true, directory, null, $"{directory} was removed.");

    public List<string> Inspected { get; } = [];

    public List<IPackageInspection> Installed { get; } = [];

    public List<string> Removed { get; } = [];

    public bool TryInspect(string zipPath, [NotNullWhen(true)] out IPackageInspection? inspection, out string failure)
    {
        Inspected.Add(zipPath);

        if (InspectionThrows is { } thrown)
        {
            throw thrown;
        }

        inspection = Inspection;
        failure = Inspection is null ? InspectionFailure : string.Empty;
        return Inspection is not null;
    }

    public PluginInstallResult Install(IPackageInspection inspection)
    {
        Installed.Add(inspection);
        return InstallResult(inspection);
    }

    public PluginInstallResult Remove(string directoryName)
    {
        Removed.Add(directoryName);
        return RemoveResult(directoryName);
    }
}
