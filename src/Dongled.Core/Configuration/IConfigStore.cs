namespace Dongled.Core.Configuration;

/// <summary>Reads and writes user configuration. The UI is the only writer.</summary>
public interface IConfigStore
{
    /// <summary>
    /// Read the configuration, returning defaults if the file is missing or unusable. Never
    /// throws: a machine that cannot read its configuration must still start.
    /// </summary>
    AppConfig Load();

    /// <summary>Write the configuration atomically.</summary>
    /// <remarks>
    /// Unlike <see cref="Load"/>, this deliberately does not fail soft. A save that silently did
    /// nothing would leave the UI showing settings the user believes are stored, so a failed write
    /// propagates and the caller is expected to tell them.
    /// </remarks>
    /// <exception cref="System.IO.IOException">
    /// The write failed. The file still holds its previous content.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The file is read-only or an ACL denies the write. The file still holds its previous
    /// content. This type does not derive from <see cref="System.IO.IOException"/>, so a caller
    /// that catches only that one will not catch this.
    /// </exception>
    void Save(AppConfig config);
}
