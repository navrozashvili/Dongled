using System.Collections.Generic;
using System.Threading.Tasks;
using Dongled.Abstractions;
using Dongled.Core.Pipeline;
using Dongled.Core.Plugins;

namespace Dongled.App.Services;

/// <summary>What the loader decided about one directory under the plugins root.</summary>
/// <param name="Directory">The directory name, which is also its configuration key.</param>
/// <param name="Status">The loader's verdict.</param>
/// <param name="Message">The loader's one-sentence explanation.</param>
/// <param name="Sha256">The directory's manifest hash, if one could be computed.</param>
/// <param name="Metadata">The provider's self-description, if it loaded and gave one.</param>
/// <param name="IsBundled">
/// Whether the directory holds exactly the files this build of Dongled shipped, which the loader
/// trusts without an approval.
/// </param>
internal sealed record PluginListing(
    string Directory,
    PluginLoadStatus Status,
    string Message,
    string? Sha256,
    ProviderMetadata? Metadata,
    bool IsBundled = false);

/// <summary>The part of <see cref="PluginHost"/> the app's pages use.</summary>
/// <remarks>Separate from the host so that view models can be tested without loading plugins.</remarks>
internal interface IPluginRuntime
{
    /// <summary>Completes once loading has finished and <see cref="Listings"/> is filled.</summary>
    Task Started { get; }

    /// <summary>
    /// One entry per directory the loader found at startup. Loading happens once, so this does not
    /// change while the app runs.
    /// </summary>
    IReadOnlyList<PluginListing> Listings { get; }

    /// <summary>Where a directory's provider is in its lifecycle, or null if it produced none.</summary>
    ProviderRunState? StateOf(string directory);

    /// <summary>Re-read the per-plugin log levels after configuration has been saved.</summary>
    void ReloadConfiguration();
}

/// <summary><see cref="IPluginRuntime"/> over the real <see cref="PluginHost"/>.</summary>
internal sealed class PluginHostRuntime : IPluginRuntime
{
    private readonly PluginHost _host;

    /// <param name="host">The running host.</param>
    public PluginHostRuntime(PluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        _host = host;
    }

    /// <inheritdoc />
    public Task Started => _host.Started;

    /// <inheritdoc />
    public IReadOnlyList<PluginListing> Listings
    {
        get
        {
            var listings = new List<PluginListing>(_host.Results.Count);

            foreach (var result in _host.Results)
            {
                listings.Add(new PluginListing(
                    result.Directory,
                    result.Status,
                    result.Message,
                    result.Manifest?.Sha256,
                    MetadataOf(result),
                    result.IsBundled));
            }

            return listings;
        }
    }

    /// <inheritdoc />
    public ProviderRunState? StateOf(string directory) => _host.StateOf(directory);

    /// <inheritdoc />
    public void ReloadConfiguration() => _host.ReloadConfiguration();

    private static ProviderMetadata? MetadataOf(PluginLoadResult result)
    {
        try
        {
            // Reading Metadata calls into plugin code, which can throw.
            return result.Plugin?.Provider.Metadata;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
