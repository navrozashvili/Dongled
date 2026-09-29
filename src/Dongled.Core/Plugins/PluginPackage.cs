using System.IO;

namespace Dongled.Core.Plugins;

/// <summary>
/// A plugin package that has been validated, extracted to staging, and hashed — but not installed.
/// </summary>
/// <remarks>
/// Owns its staging folder. Disposing deletes it, so an abandoned dialog leaves nothing behind;
/// installing moves the content away first.
/// </remarks>
public sealed class PluginPackage : IDisposable
{
    private readonly string _stagingPath;
    private bool _disposed;

    /// <param name="directoryName">The folder this would become under the plugins root.</param>
    /// <param name="assemblyName">The candidate assembly's simple name.</param>
    /// <param name="stagingPath">The staging folder this package owns and deletes on disposal.</param>
    /// <param name="contentPath">
    /// Where the plugin's files are: <paramref name="stagingPath"/> itself, or the single folder
    /// inside it when the zip wrapped everything in one.
    /// </param>
    /// <param name="manifest">The manifest of the extracted files.</param>
    internal PluginPackage(
        string directoryName,
        string assemblyName,
        string stagingPath,
        string contentPath,
        PluginManifest manifest)
    {
        DirectoryName = directoryName;
        AssemblyName = assemblyName;
        _stagingPath = stagingPath;
        ContentPath = contentPath;
        Manifest = manifest;
    }

    /// <summary>The folder this would become under the plugins root, and its configuration key.</summary>
    public string DirectoryName { get; }

    /// <summary>
    /// The candidate assembly's simple name. This is what decides whether an existing folder of the
    /// same name holds the same plugin or a different one.
    /// </summary>
    public string AssemblyName { get; }

    /// <summary>Where the plugin's extracted files are sitting now.</summary>
    public string ContentPath { get; }

    /// <summary>The manifest of the extracted files.</summary>
    public PluginManifest Manifest { get; }

    /// <summary>The hash of the extracted files.</summary>
    public string Sha256 => Manifest.Sha256;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (Directory.Exists(_stagingPath))
            {
                Directory.Delete(_stagingPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover staging folder is swept at the next start.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
