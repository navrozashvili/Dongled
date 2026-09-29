using Dongled.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Dongled.App.Pages;

/// <summary>The rules list and editor.</summary>
internal sealed partial class RulesPage : Page
{
    public RulesPage() => InitializeComponent();

    /// <summary>Null until the page has been navigated to; every binding to it is null-tolerant.</summary>
    internal RulesViewModel? ViewModel { get; private set; }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Parameter is not PageContext context)
        {
            return;
        }

        ViewModel = context.ViewModels.CreateRules();
        Bindings.Update();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // Disposing writes any name or pattern still waiting to be saved, so leaving the page does
        // not discard it.
        ViewModel?.Dispose();
        ViewModel = null;
    }
}
