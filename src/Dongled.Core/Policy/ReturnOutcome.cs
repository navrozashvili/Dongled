namespace Dongled.Core.Policy;

/// <summary>How applying a rule's disconnect behaviour ended.</summary>
internal enum ReturnOutcome
{
    /// <summary>Done, or nothing to do and nothing worth waiting for.</summary>
    Settled,

    /// <summary>
    /// The device the rule would return to is not connected right now. Worth trying again when an
    /// endpoint becomes active.
    /// </summary>
    WaitingForDevice,
}
