using System.Collections.Generic;
using System.IO;
using Dongled.Core.Configuration;

namespace Dongled.Core.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IStateStore"/> that applies the same target-role rule the real one
/// does, so a test reading <see cref="Entries"/> sees what would have been persisted.
/// </summary>
public sealed class RecordingStateStore : IStateStore
{
    /// <summary>What is currently recorded, by source identifier.</summary>
    public Dictionary<string, PreviousDefault> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every capture that was attempted, with the arguments it was given.</summary>
    public List<(string SourceId, string? MultimediaId, string? CommunicationsId, string TargetDeviceId)> CaptureCalls
    { get; } = [];

    /// <summary>Makes <see cref="CapturePrevious"/> fail, as a read-only state file would.</summary>
    public bool ThrowOnCapture { get; set; }

    /// <inheritdoc />
    public PreviousDefault? GetPrevious(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        return Entries.GetValueOrDefault(sourceId);
    }

    /// <inheritdoc />
    public void CapturePrevious(string sourceId, string? multimediaId, string? communicationsId, string targetDeviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceId);

        CaptureCalls.Add((sourceId, multimediaId, communicationsId, targetDeviceId));

        if (ThrowOnCapture)
        {
            throw new IOException("Fake state store failure.");
        }

        var multimedia = Keep(multimediaId, targetDeviceId);
        var communications = Keep(communicationsId, targetDeviceId);

        if (multimedia is null && communications is null)
        {
            return;
        }

        Entries[sourceId] = new PreviousDefault(multimedia, communications, DateTimeOffset.UnixEpoch);
    }

    /// <inheritdoc />
    public void ClearPrevious(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        Entries.Remove(sourceId);
    }

    private static string? Keep(string? candidate, string targetDeviceId) =>
        string.IsNullOrWhiteSpace(candidate)
        || string.Equals(candidate, targetDeviceId, StringComparison.OrdinalIgnoreCase)
            ? null
            : candidate;
}
