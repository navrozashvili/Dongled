using Dongled.App.Services;
using Dongled.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Dongled.App.Pages;

/// <summary>The plugin list, and the flows for adding, approving and removing plugins.</summary>
internal sealed partial class PluginsPage : Page
{
    public PluginsPage() => InitializeComponent();

    /// <summary>Null until the page has been navigated to; every binding to it is null-tolerant.</summary>
    internal PluginsViewModel? ViewModel { get; private set; }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Parameter is not PageContext context)
        {
            return;
        }

        ViewModel = context.ViewModels.CreatePlugins(new ContentDialogService(this, context.WindowHandle));
        Bindings.Update();
    }
}
