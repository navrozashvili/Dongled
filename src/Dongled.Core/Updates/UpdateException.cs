namespace Dongled.Core.Updates;

/// <summary>An update step failed for a reason a user can be told in one sentence.</summary>
/// <remarks>The message is written to be shown as is.</remarks>
public sealed class UpdateException : Exception
{
    public UpdateException()
    {
    }

    public UpdateException(string message)
        : base(message)
    {
    }

    public UpdateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
