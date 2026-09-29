using System.Collections.Generic;
using System.Threading.Tasks;

namespace Dongled.App.Services;

/// <summary>A labelled value shown in a dialog, such as a folder name or a hash.</summary>
/// <param name="Label">The caption above the value.</param>
/// <param name="Value">The value, selectable so it can be copied.</param>
/// <param name="Monospace">Whether to show it in a fixed-width font, for hashes and file lists.</param>
internal sealed record DialogField(string Label, string Value, bool Monospace = false);

/// <summary>A question with one button that goes ahead and one that does not.</summary>
/// <param name="Title">The dialog's title.</param>
/// <param name="Message">The paragraph under the title.</param>
/// <param name="ConfirmButton">The text of the button that goes ahead.</param>
/// <param name="CancelButton">The text of the button that does not.</param>
internal sealed record ConfirmationRequest(string Title, string Message, string ConfirmButton, string CancelButton)
{
    /// <summary>Labelled values shown under the message.</summary>
    public IReadOnlyList<DialogField> Fields { get; init; } = [];

    /// <summary>Whether the confirm button can be pressed at all.</summary>
    public bool CanConfirm { get; init; } = true;
}

/// <summary>Modal questions and messages, and the file picker.</summary>
/// <remarks>
/// View models ask through this so that the flows they run can be tested without a window. Every
/// confirmation defaults to the button that does not go ahead.
/// </remarks>
internal interface IDialogService
{
    /// <summary>Ask a question.</summary>
    /// <returns>Whether the user pressed the confirm button.</returns>
    Task<bool> ConfirmAsync(ConfirmationRequest request);

    /// <summary>Say something that needs only a Close button.</summary>
    Task ShowMessageAsync(string title, string message);

    /// <summary>Let the user choose one file.</summary>
    /// <param name="commitButton">The text of the picker's open button.</param>
    /// <param name="fileTypeName">What the file-type dropdown calls the accepted files.</param>
    /// <param name="extension">The one extension offered, with its dot.</param>
    /// <returns>The chosen path, or <see langword="null"/> if the user cancelled.</returns>
    Task<string?> PickFileAsync(string commitButton, string fileTypeName, string extension);
}
