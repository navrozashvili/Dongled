using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Dongled.Core.Plugins;

namespace Dongled.App.Services;

/// <summary>A package that has been extracted to staging and judged against the plugins directory.</summary>
/// <remarks>
/// Owns the extracted files until it is installed or disposed, so a cancelled install leaves
/// nothing behind. See <see cref="PluginInspection"/>.
/// </remarks>
internal interface IPackageInspection : IDisposable
{
    /// <summary>What installing this package would mean for the folder it names.</summary>
    PluginInstallOutcome Outcome { get; }

    /// <summary>The folder this would become, which is also its configuration key.</summary>
    string DirectoryName { get; }

    /// <summary>The plugin assembly's simple name.</summary>
    string AssemblyName { get; }

    /// <summary>For an upgrade, which files differ from the installed copy. Empty otherwise.</summary>
    IReadOnlyList<string> DifferingFiles { get; }

    /// <summary>Whether the plugin it would replace is running, and so has its files open.</summary>
    bool IsInstalledPluginLoaded { get; }

    /// <summary>One sentence describing <see cref="Outcome"/>.</summary>
    string Message { get; }
}

/// <summary>The part of <see cref="PluginInstaller"/> the Plugins page uses.</summary>
/// <remarks>Separate from the installer so that the page's flows can be tested without touching disk.</remarks>
internal interface IPluginInstaller
{
    /// <summary>Extract and judge an archive.</summary>
    /// <param name="zipPath">The file the user picked.</param>
    /// <param name="inspection">The judgement, which the caller must dispose.</param>
    /// <param name="failure">Why the file is not a plugin package. Empty on success.</param>
    bool TryInspect(string zipPath, [NotNullWhen(true)] out IPackageInspection? inspection, out string failure);

    /// <summary>Move an inspected package into the plugins directory.</summary>
    /// <exception cref="InvalidOperationException">
    /// The inspection's outcome is <see cref="PluginInstallOutcome.AlreadyInstalled"/> or
    /// <see cref="PluginInstallOutcome.Conflict"/>, which leave nothing to install.
    /// </exception>
    PluginInstallResult Install(IPackageInspection inspection);

    /// <summary>Delete a plugin's folder and its configuration entry.</summary>
    PluginInstallResult Remove(string directoryName);
}

/// <summary><see cref="IPluginInstaller"/> over the real <see cref="PluginInstaller"/>.</summary>
internal sealed class CorePluginInstaller : IPluginInstaller
{
    private readonly PluginInstaller _installer;

    /// <param name="installer">The installer to forward to.</param>
    public CorePluginInstaller(PluginInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(installer);

        _installer = installer;
    }

    /// <inheritdoc />
    public bool TryInspect(string zipPath, [NotNullWhen(true)] out IPackageInspection? inspection, out string failure)
    {
        PluginInspection? inner = null;

        try
        {
            if (_installer.TryInspect(zipPath, out inner, out failure) && inner is not null)
            {
                inspection = new Inspection(inner);

                // Ownership has passed to the wrapper.
                inner = null;
                return true;
            }

            inspection = null;
            return false;
        }
        finally
        {
            inner?.Dispose();
        }
    }

    /// <inheritdoc />
    public PluginInstallResult Install(IPackageInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);

        if (inspection is not Inspection wrapped)
        {
            throw new ArgumentException("Only an inspection made by this installer can be installed.", nameof(inspection));
        }

        return _installer.Install(wrapped.Inner);
    }

    /// <inheritdoc />
    public PluginInstallResult Remove(string directoryName) => _installer.Remove(directoryName);

    private sealed class Inspection(PluginInspection inner) : IPackageInspection
    {
        public PluginInspection Inner { get; } = inner;

        public PluginInstallOutcome Outcome => Inner.Outcome;

        public string DirectoryName => Inner.DirectoryName;

        public string AssemblyName => Inner.AssemblyName;

        public IReadOnlyList<string> DifferingFiles => Inner.DifferingFiles;

        public bool IsInstalledPluginLoaded => Inner.IsInstalledPluginLoaded;

        public string Message => Inner.Message;

        // Safe after an install: the package checks whether its staging folder is still there.
        public void Dispose() => Inner.Dispose();
    }
}
