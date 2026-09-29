using Dongled.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Dongled.App.Pages;

/// <summary>The reorderable battery list. Writes <c>config.json</c> on every reorder.</summary>
internal sealed partial class BatteryPage : Page
{
    public BatteryPage() => InitializeComponent();

    /// <summary>
    /// Null until the page has been navigated to. x:Bind skips a path whose root is null, so the
    /// page renders empty until <see cref="OnNavigatedTo"/> supplies one and updates the bindings.
    /// </summary>
    internal BatteryViewModel? ViewModel { get; private set; }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Parameter is not PageContext context)
        {
            return;
        }

        ViewModel = context.ViewModels.CreateBattery();
        Bindings.Update();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // Releases the view model's poll timer, which would otherwise outlive the page.
        ViewModel?.Dispose();
        ViewModel = null;
    }
}
