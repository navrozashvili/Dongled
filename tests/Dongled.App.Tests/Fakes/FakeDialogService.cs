using Dongled.App.Services;

namespace Dongled.App.Tests.Fakes;

/// <summary>Answers dialogs from a script and records what was asked.</summary>
internal sealed class FakeDialogService : IDialogService
{
    private readonly Queue<Func<Task<bool>>> _answers = new();

    public List<ConfirmationRequest> Confirmations { get; } = [];

    public List<(string Title, string Message)> Messages { get; } = [];

    /// <summary>What the file picker returns; null means the user cancelled.</summary>
    public string? PickedFile { get; set; }

    /// <summary>When set, the next <see cref="ConfirmAsync"/> throws it.</summary>
    public Exception? ConfirmFailure { get; set; }

    /// <summary>Answer the next confirmation with <paramref name="answer"/>.</summary>
    public FakeDialogService Answer(bool answer)
    {
        _answers.Enqueue(() => Task.FromResult(answer));
        return this;
    }

    /// <summary>Answer the next confirmation when <paramref name="answer"/> completes.</summary>
    public FakeDialogService AnswerLater(Task<bool> answer)
    {
        _answers.Enqueue(() => answer);
        return this;
    }

    public Task<bool> ConfirmAsync(ConfirmationRequest request)
    {
        Confirmations.Add(request);

        if (ConfirmFailure is { } failure)
        {
            ConfirmFailure = null;
            throw failure;
        }

        return _answers.Count > 0
            ? _answers.Dequeue()()
            : throw new InvalidOperationException($"No answer scripted for \"{request.Title}\".");
    }

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add((title, message));
        return Task.CompletedTask;
    }

    public Task<string?> PickFileAsync(string commitButton, string fileTypeName, string extension) =>
        Task.FromResult(PickedFile);
}
