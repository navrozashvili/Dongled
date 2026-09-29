using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;

namespace Dongled.App.Services;

/// <summary><see cref="IDialogService"/> using WinUI's <see cref="ContentDialog"/> and file picker.</summary>
internal sealed class ContentDialogService : IDialogService
{
    private readonly UIElement _owner;
    private readonly nint _windowHandle;

    /// <param name="owner">
    /// The element the dialogs are shown over. Its <see cref="UIElement.XamlRoot"/> is read when a
    /// dialog opens, because it is not set until the element is in the live tree.
    /// </param>
    /// <param name="windowHandle">
    /// The window's HWND. An unpackaged app's file picker has no parent window of its own and
    /// throws without one.
    /// </param>
    public ContentDialogService(UIElement owner, nint windowHandle)
    {
        ArgumentNullException.ThrowIfNull(owner);

        _owner = owner;
        _windowHandle = windowHandle;
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(ConfirmationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var message = new TextBlock { Text = request.Message, TextWrapping = TextWrapping.Wrap };
        object content = message;

        if (request.Fields.Count > 0)
        {
            var body = new StackPanel { Spacing = 12 };
            body.Children.Add(message);

            foreach (var field in request.Fields)
            {
                body.Children.Add(new TextBlock { Text = field.Label, Opacity = 0.6 });

                var value = new TextBlock
                {
                    Text = field.Value,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                };

                if (field.Monospace)
                {
                    value.FontFamily = new FontFamily("Consolas");
                }

                body.Children.Add(value);
            }

            // Scrollable because a list of changed files can be longer than the window.
            content = new ScrollViewer { Content = body };
        }

        var dialog = new ContentDialog
        {
            XamlRoot = _owner.XamlRoot,
            Title = request.Title,
            Content = content,
            PrimaryButtonText = request.ConfirmButton,
            CloseButtonText = request.CancelButton,
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = request.CanConfirm,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <inheritdoc />
    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = _owner.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "Close",
        };

        await dialog.ShowAsync();
    }

    /// <inheritdoc />
    public async Task<string?> PickFileAsync(string commitButton, string fileTypeName, string extension)
    {
        // The Windows App SDK picker rather than Windows.Storage.Pickers: in an unpackaged app the
        // older picker ignores its file-type filter and offers every file. This one takes its owner
        // window up front and honours the filter.
        var picker = new FileOpenPicker(Win32Interop.GetWindowIdFromWindow(_windowHandle))
        {
            CommitButtonText = commitButton,
        };

        // FileTypeChoices rather than FileTypeFilter, because it carries the name the dropdown
        // shows. Setting both would list the same filter twice.
        picker.FileTypeChoices.Add(fileTypeName, [extension]);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
