namespace Dongled.App.ViewModels;

/// <summary>
/// One option in a source or device dropdown: an identifier the app uses and a label the user reads.
/// </summary>
/// <param name="Id">The identifier, which is never displayed. Empty means "nothing chosen".</param>
/// <param name="Name">
/// The bare display name, with no decoration. This is the value written to <c>lastKnownName</c>.
/// </param>
/// <param name="Label">
/// What the user sees, which may carry a qualifier such as "— not currently connected".
/// </param>
/// <remarks>
/// The name and the label are kept separate because only the name may be stored. Persisting the
/// label would write the qualifier into <c>lastKnownName</c>, and the next render would append it
/// a second time.
/// </remarks>
internal sealed record ChoiceItem(string Id, string Name, string Label)
{
    /// <inheritdoc />
    /// <remarks>
    /// A ComboBox without an item template shows whatever this returns, and that must never be the
    /// identifier.
    /// </remarks>
    public override string ToString() => Label;
}
