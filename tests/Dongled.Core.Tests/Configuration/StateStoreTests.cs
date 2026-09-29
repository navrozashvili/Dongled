using System.IO;
using Dongled.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Dongled.Core.Tests.Configuration;

public sealed class StateStoreTests : IDisposable
{
    private const string Source = "hyperx:cloud-iii-s-wireless:any";
    private const string Target = "{0.0.0.00000000}.{target}";
    private const string Speakers = "{0.0.0.00000000}.{speakers}";
    private const string Monitor = "{0.0.0.00000000}.{monitor}";

    private const string NewerSchemaFile = $$"""
        {
          "schemaVersion": 99,
          "previousDefaults": { "{{Source}}": { "multimediaId": "{{Speakers}}", "communicationsId": null, "capturedUtc": "2026-07-26T12:00:00+00:00" } }
        }
        """;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ass-state-" + Guid.NewGuid().ToString("N"));

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));

    public StateStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string StatePath => Path.Combine(_directory, "state.json");

    private StateStore CreateStore() => new(StatePath, _time, NullLogger<StateStore>.Instance);

    [Fact]
    public void Nothing_is_remembered_before_anything_is_captured()
    {
        Assert.Null(CreateStore().GetPrevious(Source));
    }

    [Fact]
    public void A_capture_survives_a_reload()
    {
        CreateStore().CapturePrevious(Source, Speakers, Monitor, Target);

        var reloaded = CreateStore().GetPrevious(Source);

        Assert.NotNull(reloaded);
        Assert.Equal(Speakers, reloaded.MultimediaId);
        Assert.Equal(Monitor, reloaded.CommunicationsId);
        Assert.Equal(_time.GetUtcNow(), reloaded.CapturedUtc);
    }

    [Fact]
    public void Capturing_drops_a_role_already_sitting_on_the_rules_own_target()
    {
        // Without this the target becomes its own previous, so on disconnect the
        // rule "restores" to the device it was switching away from and the return silently
        // does nothing.
        CreateStore().CapturePrevious(Source, multimediaId: Target, communicationsId: Speakers, targetDeviceId: Target);

        var stored = CreateStore().GetPrevious(Source);

        Assert.NotNull(stored);
        Assert.Null(stored.MultimediaId);
        Assert.Equal(Speakers, stored.CommunicationsId);
    }

    [Fact]
    public void Capturing_drops_the_communications_role_when_it_is_the_rules_own_target()
    {
        // The mirror of the multimedia case above, asserted rather than assumed by symmetry:
        // this is the branch that makes a communications-only return work at all.
        CreateStore().CapturePrevious(Source, multimediaId: Speakers, communicationsId: Target, targetDeviceId: Target);

        var stored = CreateStore().GetPrevious(Source);

        Assert.NotNull(stored);
        Assert.Null(stored.CommunicationsId);
        Assert.Equal(Speakers, stored.MultimediaId);
    }

    [Fact]
    public void Sanitization_ignores_identifier_casing()
    {
        CreateStore().CapturePrevious(Source, multimediaId: Target.ToUpperInvariant(), communicationsId: null, targetDeviceId: Target);

        Assert.Null(CreateStore().GetPrevious(Source)?.MultimediaId);
    }

    [Fact]
    public void Capturing_nothing_useful_records_nothing()
    {
        // Both roles already sit on the target, so there is nothing to come back to. An entry
        // of two nulls would make a later restore look possible when it is not.
        CreateStore().CapturePrevious(Source, Target, Target, Target);

        Assert.Null(CreateStore().GetPrevious(Source));
    }

    [Fact]
    public void A_later_capture_replaces_an_earlier_one()
    {
        var store = CreateStore();
        store.CapturePrevious(Source, Speakers, Speakers, Target);

        _time.Advance(TimeSpan.FromMinutes(5));
        store.CapturePrevious(Source, Monitor, Monitor, Target);

        var stored = store.GetPrevious(Source);
        Assert.NotNull(stored);
        Assert.Equal(Monitor, stored.MultimediaId);
        Assert.Equal(_time.GetUtcNow(), stored.CapturedUtc);
    }

    [Fact]
    public void Clearing_removes_only_the_named_source()
    {
        var store = CreateStore();
        store.CapturePrevious(Source, Speakers, Speakers, Target);
        store.CapturePrevious("other:source", Monitor, Monitor, Target);

        store.ClearPrevious(Source);

        Assert.Null(store.GetPrevious(Source));
        Assert.NotNull(store.GetPrevious("other:source"));
    }

    [Fact]
    public void Sources_are_matched_case_insensitively()
    {
        CreateStore().CapturePrevious(Source, Speakers, Speakers, Target);

        Assert.NotNull(CreateStore().GetPrevious(Source.ToUpperInvariant()));
    }

    [Fact]
    public void Reading_a_state_file_from_a_newer_schema_version_ignores_it_and_leaves_it_alone()
    {
        File.WriteAllText(StatePath, NewerSchemaFile);

        Assert.Null(CreateStore().GetPrevious(Source));
        Assert.Equal(NewerSchemaFile, File.ReadAllText(StatePath));
    }

    [Fact]
    public void Capturing_over_a_newer_schema_state_file_copies_it_aside_first()
    {
        // Reading a newer file leaves it alone, but returns empty state, so the first capture
        // after a downgrade writes this build's shape straight over a file the user never saw.
        File.WriteAllText(StatePath, NewerSchemaFile);

        CreateStore().CapturePrevious(Source, Speakers, null, Target);

        Assert.Equal(NewerSchemaFile, File.ReadAllText(StatePath + ".newer-v99.bak"));
    }

    [Fact]
    public void Capturing_over_a_same_or_older_schema_state_file_leaves_no_backup()
    {
        File.WriteAllText(StatePath, """{ "schemaVersion": 1, "previousDefaults": {} }""");

        CreateStore().CapturePrevious(Source, Speakers, null, Target);

        Assert.Empty(Directory.GetFiles(_directory, "*.bak"));
    }

    [Fact]
    public void Hand_edited_entries_naming_no_device_are_dropped()
    {
        // Neither shape is reachable while this store is the sole writer. A null value is a null
        // inside a dictionary every consumer types as non-nullable, and an entry naming no device
        // makes a later restore look possible when it is not.
        File.WriteAllText(StatePath, """
        {
          "schemaVersion": 1,
          "previousDefaults": { "null-entry": null, "empty-entry": {} }
        }
        """);

        var store = CreateStore();

        Assert.Null(store.GetPrevious("null-entry"));
        Assert.Null(store.GetPrevious("empty-entry"));
    }

    [Fact]
    public void A_file_naming_one_source_twice_under_different_casing_reads_as_empty_rather_than_throwing()
    {
        // JSON permits a duplicate name and the serializer takes the last, so this loads as a
        // dictionary of two keys; rebuilding it with an ordinal-ignore-case comparer then throws
        // ArgumentException on the duplicate. That is not a JsonException, so it is only the
        // breadth of the catch that keeps the read from throwing - which is what this pins.
        File.WriteAllText(StatePath, """
        {
          "schemaVersion": 1,
          "previousDefaults": {
            "SRC": { "multimediaId": "a", "capturedUtc": "2026-07-26T12:00:00+00:00" },
            "src": { "multimediaId": "b", "capturedUtc": "2026-07-26T12:00:00+00:00" }
          }
        }
        """);

        var store = CreateStore();

        Assert.Null(store.GetPrevious("SRC"));
        Assert.Null(store.GetPrevious("src"));

        // And the store is still usable afterwards, rather than wedged on a file it cannot read.
        store.CapturePrevious(Source, Speakers, null, Target);
        Assert.NotNull(store.GetPrevious(Source));
    }

    [Fact]
    public void Capturing_over_a_newer_schema_state_file_spelled_with_different_casing_still_copies_it_aside()
    {
        // The state store's half of the same casing hazard the configuration store has: Load
        // recognizes this file as newer, so the save guard must too.
        var original = NewerSchemaFile.Replace("\"schemaVersion\"", "\"SchemaVersion\"", StringComparison.Ordinal);
        File.WriteAllText(StatePath, original);

        var store = CreateStore();
        Assert.Null(store.GetPrevious(Source));

        store.CapturePrevious(Source, Speakers, null, Target);

        Assert.Equal(original, File.ReadAllText(StatePath + ".newer-v99.bak"));
    }

    [Fact]
    public void Corrupt_state_falls_back_to_empty_and_stays_usable()
    {
        File.WriteAllText(StatePath, "{ not json at all");

        var store = CreateStore();

        Assert.Null(store.GetPrevious(Source));

        store.CapturePrevious(Source, Speakers, null, Target);
        Assert.NotNull(store.GetPrevious(Source));
    }
}
