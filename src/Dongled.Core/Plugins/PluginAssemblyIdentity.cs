using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Dongled.Core.Plugins;

/// <summary>
/// What a candidate assembly says about itself, read from its metadata without loading it.
/// </summary>
/// <remarks>
/// <para>
/// Two callers need byte-identical reads: the loader, deciding whether an approved directory may
/// run, and the installer, deciding whether an incoming package is worth putting on disk at all. A
/// second copy of this sequence would be free to drift, and the two disagreeing would mean a
/// package that installs cleanly and is then refused at every start.
/// </para>
/// <para>
/// Takes a <see cref="Stream"/> rather than a path on purpose. The loader opens the candidate once,
/// denying writers, and holds that one handle from the identity check through hashing and loading;
/// a path-based overload here would invite it to open the file a second time and leave a window in
/// which the bytes could be swapped.
/// </para>
/// </remarks>
/// <param name="Name">The assembly's simple name, for example <c>Dongled.Plugin.HyperXHid</c>.</param>
/// <param name="Version">The assembly's own version.</param>
/// <param name="SdkVersion">
/// The version of <c>Dongled.Abstractions</c> it references, or <see langword="null"/>
/// if it does not reference it at all — which means it was not built against the plugin SDK.
/// </param>
internal sealed record PluginAssemblyIdentity(string Name, Version Version, Version? SdkVersion)
{
    private const string SdkAssemblyName = "Dongled.Abstractions";

    /// <summary>Read identity from a candidate assembly's bytes.</summary>
    /// <param name="stream">
    /// Seekable, positioned anywhere; it is rewound first and left open and rewindable.
    /// </param>
    /// <param name="identity">The identity, when this returns <see langword="true"/>.</param>
    /// <param name="failure">
    /// A sentence fragment naming what is wrong, phrased to follow the file's name: callers write
    /// <c>$"'{fileName}' {failure}"</c>. Empty on success.
    /// </param>
    public static bool TryRead(Stream stream, out PluginAssemblyIdentity? identity, out string failure)
    {
        ArgumentNullException.ThrowIfNull(stream);

        identity = null;
        stream.Seek(0, SeekOrigin.Begin);

        try
        {
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);

            // A native DLL is a perfectly valid PE file with no metadata, so this is not an
            // exception path and checking only for BadImageFormatException would miss it.
            if (!pe.HasMetadata)
            {
                failure = "is not a managed assembly.";
                return false;
            }

            var metadata = pe.GetMetadataReader();
            var definition = metadata.GetAssemblyDefinition();

            identity = new PluginAssemblyIdentity(
                metadata.GetString(definition.Name),
                definition.Version,
                ReadSdkReference(metadata));

            failure = string.Empty;
            return true;
        }
        catch (BadImageFormatException ex)
        {
            failure = $"is not a readable assembly: {ex.Message}";
            return false;
        }
    }

    private static Version? ReadSdkReference(MetadataReader metadata)
    {
        foreach (var handle in metadata.AssemblyReferences)
        {
            var reference = metadata.GetAssemblyReference(handle);
            if (string.Equals(metadata.GetString(reference.Name), SdkAssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                return reference.Version;
            }
        }

        return null;
    }
}
