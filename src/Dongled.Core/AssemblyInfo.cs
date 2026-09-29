using System.Runtime.CompilerServices;

// The interop declarations, the catalogue, the scheduler and the policy actions are internal
// because they are not part of any consumer's surface. Making them public just to test them
// would trade CA1812 for CA1515 and publish a shape the app must not bind to.
[assembly: InternalsVisibleTo("Dongled.Core.Tests")]
