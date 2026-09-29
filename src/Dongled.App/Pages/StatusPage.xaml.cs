using Dongled.App.Services;
using Dongled.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Dongled.App.Pages;

/// <summary>The landing page: current defaults, detected sources and recent activity. Read-only.</summary>
internal sealed partial class StatusPage : Page
{
    public StatusPage() => InitializeComponent();

    /// <summary>
    /// Null until the page has been navigated to. x:Bind skips a path whose root is null, so the
    /// page renders empty until <see cref="OnNavigatedTo"/> supplies one and updates the bindings.
    /// </summary>
    internal StatusViewModel? ViewModel { get; private set; }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Parameter is not PageContext context)
        {
            return;
        }

        ViewModel = context.ViewModels.CreateStatus(new ContentDialogService(this, context.WindowHandle));
        Bindings.Update();
    }

    /// <inheritdoc />
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // A page is constructed afresh on each navigation, so the view model's timer and event
        // subscriptions have to be released here or one set would accumulate per visit.
        ViewModel?.Dispose();
        ViewModel = null;
    }

    /// <summary>The banner's close button: remember that this release was dismissed.</summary>
    private void OnUpdateBannerCloseClick(InfoBar sender, object args) =>
        ViewModel?.DismissUpdateCommand.Execute(null);
}
