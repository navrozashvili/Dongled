using System.ComponentModel;
using Dongled.App.ViewModels;
using H.NotifyIcon;

// Aliased because System.Drawing.Icon is the only type needed from that namespace.
using Icon = System.Drawing.Icon;

namespace Dongled.App.Tray;

/// <summary>Keeps the notification-area control showing the icon the shell view model renders.</summary>
/// <remarks>
/// Done in code because <c>TaskbarIcon.Icon</c> is a plain CLR property, which <c>x:Bind</c> cannot
/// target.
/// </remarks>
internal sealed class TrayIconBinding
{
    private readonly TaskbarIcon _control;
    private readonly ShellViewModel _shell;

    /// <param name="control">The notification-area control.</param>
    /// <param name="shell">The view model whose <see cref="ShellViewModel.TrayIcon"/> it shows.</param>
    public TrayIconBinding(TaskbarIcon control, ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(shell);

        _control = control;
        _shell = shell;

        _shell.PropertyChanged += OnShellPropertyChanged;
        _control.Icon = ForControl(_shell.TrayIcon);
    }

    /// <summary>Stop following the view model and clear the icon, releasing the last copy.</summary>
    public void Detach()
    {
        _shell.PropertyChanged -= OnShellPropertyChanged;
        _control.Icon = null;
    }

    /// <summary>A copy of <paramref name="icon"/> that the control may own and dispose.</summary>
    /// <remarks>
    /// <para>
    /// The renderer owns every icon it returns and hands the same instance back whenever a state
    /// recurs. <c>TaskbarIcon.Icon</c>, on the other hand, disposes the value it displaces. Given
    /// the renderer's own instance, the control would destroy it when replaced, and the next time
    /// that state recurred the renderer would serve a disposed icon, which throws from inside the
    /// battery refresh and ends the process.
    /// </para>
    /// <para>
    /// <see cref="Icon.Clone"/> duplicates the underlying icon handle, so the control can destroy
    /// its copy while the renderer's stays whole. Each copy is disposed when the next displaces it,
    /// and <see cref="Detach"/> clears the last.
    /// </para>
    /// </remarks>
    private static Icon? ForControl(Icon? icon) => (Icon?)icon?.Clone();

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.TrayIcon))
        {
            _control.Icon = ForControl(_shell.TrayIcon);
        }
    }
}
