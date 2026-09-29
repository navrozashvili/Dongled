using Dongled.App.ViewModels;

namespace Dongled.App.Pages;

/// <summary>What every page is handed as its navigation parameter.</summary>
/// <param name="ViewModels">Builds the page's view model.</param>
/// <param name="WindowHandle">
/// The window's HWND, which a page has no other route to. The file picker needs it as its owner.
/// </param>
internal sealed record PageContext(ViewModelFactory ViewModels, nint WindowHandle);
