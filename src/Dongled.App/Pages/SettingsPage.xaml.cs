using Dongled.App.Services;
using Dongled.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Dongled.App.Pages;

/// <summary>App settings, including per-user start with Windows.</summary>
internal sealed partial class SettingsPage : Page
{
    public SettingsPage() => InitializeComponent();

    /// <summary>Null until the page has been navigated to; every binding to it is null-tolerant.</summary>
    internal SettingsViewModel? ViewModel { get; private set; }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Parameter is not PageContext context)
        {
            return;
        }

        ViewModel = context.ViewModels.CreateSettings(new ContentDialogService(this, context.WindowHandle));
        Bindings.Update();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // The view model listens for update checks finishing; a fresh one is built on every visit.
        ViewModel?.Dispose();
        ViewModel = null;
    }
}
