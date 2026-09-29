using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Dongled.App.ViewModels;

/// <summary>The actions a row offers, shared by every row and given the row as their parameter.</summary>
/// <param name="Approve">Asks, then approves and switches the plugin on.</param>
/// <param name="Disable">Asks, then switches the plugin off.</param>
/// <param name="Remove">Asks, then deletes the plugin's folder.</param>
internal sealed record PluginRowCommands(ICommand Approve, ICommand Disable, ICommand Remove);

/// <summary>One plugin directory, as the Plugins page shows it.</summary>
internal sealed class PluginRow : ObservableObject
{
    /// <summary>
    /// The severities offered per plugin, in the order the page lists them. The default is Warning
    /// in every build configuration, so a release user's bug report shows the same lines a
    /// developer would see.
    /// </summary>
    public static readonly LogLevel[] Levels =
    [
        LogLevel.Debug,
        LogLevel.Information,
        LogLevel.Warning,
        LogLevel.Error,
    ];

    private readonly PluginRowCommands _commands;
    private int _logLevelIndex;

    /// <param name="directory">The directory name under the plugins root, and its configuration key.</param>
    /// <param name="title">The provider's display name, or the directory if it has none.</param>
    /// <param name="description">The provider's one-line description, or empty.</param>
    /// <param name="status">The short status shown beside the title.</param>
    /// <param name="message">The loader's one-sentence explanation.</param>
    /// <param name="currentSha256">The directory's manifest hash right now, if one could be computed.</param>
    /// <param name="isExperimental">Whether the provider declares itself fragile.</param>
    /// <param name="canApprove">Whether an Enable action makes sense for this row.</param>
    /// <param name="canDisable">Whether a Switch off action makes sense for this row.</param>
    /// <param name="logLevel">The configured minimum severity for this plugin.</param>
    /// <param name="commands">The page's row actions.</param>
    /// <param name="isBundled">
    /// Whether these are exactly the files shipped with this build of Dongled, which load without an
    /// approval.
    /// </param>
    public PluginRow(
        string directory,
        string title,
        string description,
        string status,
        string message,
        string? currentSha256,
        bool isExperimental,
        bool canApprove,
        bool canDisable,
        LogLevel logLevel,
        PluginRowCommands commands,
        bool isBundled = false)
    {
        ArgumentNullException.ThrowIfNull(commands);

        IsBundled = isBundled;
        Directory = directory;
        Title = title;
        Description = description;
        Status = status;
        Message = message;
        CurrentSha256 = currentSha256;
        IsExperimental = isExperimental;
        CanApprove = canApprove;
        CanDisable = canDisable;
        _commands = commands;

        var index = Array.IndexOf(Levels, logLevel);
        _logLevelIndex = index < 0 ? Array.IndexOf(Levels, LogLevel.Warning) : index;
    }

    /// <summary>Raised when the user picks a different severity for this plugin.</summary>
    public event EventHandler? LogLevelChanged;

    public string Directory { get; }

    public string Title { get; }

    public string Description { get; }

    public bool HasDescription => Description.Length > 0;

    public string Status { get; }

    public string Message { get; }

    /// <summary>What the approval dialog shows, and what approval records.</summary>
    public string? CurrentSha256 { get; }

    public bool IsExperimental { get; }

    /// <summary>
    /// Whether these are exactly the files shipped with this build of Dongled. The page marks the
    /// row, and a configuration entry created for it starts switched on, because without one the
    /// plugin was already loading.
    /// </summary>
    public bool IsBundled { get; }

    public bool CanApprove { get; }

    public bool CanDisable { get; }

    /// <summary>Whether a log level can be chosen, which only makes sense once approved.</summary>
    public bool CanChooseLogLevel => CanDisable;

    /// <summary>
    /// Whether a Remove action makes sense. Always: the page lists folders that are not plugins at
    /// all, and those are the ones a user most wants gone.
    /// </summary>
    /// <remarks>
    /// An instance property because <c>x:Bind</c> in the row template compiles against the row,
    /// and here rather than in XAML so there is one place to change if that stops being true.
    /// </remarks>
    public bool CanRemove { get; } = true;

    public ICommand ApproveCommand => _commands.Approve;

    public ICommand DisableCommand => _commands.Disable;

    public ICommand RemoveCommand => _commands.Remove;

    /// <summary>Selected severity, as an index into <see cref="Levels"/>.</summary>
    public int LogLevelIndex
    {
        get => _logLevelIndex;
        set
        {
            if (SetProperty(ref _logLevelIndex, value))
            {
                LogLevelChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The chosen severity.</summary>
    public LogLevel SelectedLogLevel =>
        _logLevelIndex >= 0 && _logLevelIndex < Levels.Length ? Levels[_logLevelIndex] : LogLevel.Warning;
}
