namespace Dongled.App.Services;

/// <summary>Progress reported from any thread, delivered on the UI thread.</summary>
/// <remarks>
/// Rather than <see cref="Progress{T}"/>, which posts to whatever synchronization context was
/// current when it was created and so behaves differently under test.
/// </remarks>
internal sealed class UiProgress<T>(IUiDispatcher dispatcher, Action<T> report) : IProgress<T>
{
    /// <inheritdoc />
    public void Report(T value) => dispatcher.TryEnqueue(() => report(value));
}
