using System.Diagnostics.CodeAnalysis;

// CA1861 flags the inline array literal that spells out the exact directory listing a failed
// atomic write must leave behind. Hoisting it to a static readonly field is the rule's fix, and it
// would move the expected value away from the assertion that reads it, which is the whole point of
// writing it inline in a test. The method runs once per test run, so the allocation the rule cares
// about does not exist here. Scoped to the one method so CA1861 keeps firing on constant arrays
// passed inside loops or hot paths elsewhere.
[assembly: SuppressMessage(
    "Performance",
    "CA1861:Avoid constant arrays as arguments",
    Scope = "member",
    Target = "~M:Dongled.Core.Tests.Configuration.AtomicFileTests.A_failed_write_does_not_leave_temporary_files_behind",
    Justification = "The literal is a test's expected value, read next to the assertion; the method runs once, so there is no repeated allocation.")]
