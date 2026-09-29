using System.Runtime.CompilerServices;

// The version lives in <Version> in the csproj and must not be hand-written here: a
// hand-written attribute both collides with the SDK-generated one (CS0579) and creates
// a second source of truth that can silently diverge from the package version.
[assembly: InternalsVisibleTo("Dongled.Core.Tests")]
