using Dongled.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Dongled.App.Pages;

/// <summary>The live log tail.</summary>
internal sealed partial class LogsPage : Page
{
    public LogsPage() => InitializeComponent();

    /// <summary>Null until the page has been navigated to; every binding to it is null-tolerant.</summary>
    internal LogsViewModel? ViewModel { get; private set; }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Parameter is not PageContext context)
        {
            return;
        }

        ViewModel = context.ViewModels.CreateLogs();
        Bindings.Update();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // The view model is subscribed to the log buffer, which outlives the page.
        ViewModel?.Dispose();
        ViewModel = null;
    }
}
