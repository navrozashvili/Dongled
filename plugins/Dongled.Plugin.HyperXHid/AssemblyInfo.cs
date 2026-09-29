using System.Runtime.CompilerServices;

// The provider's decisions are reachable only through the vendor seam, which is internal because a
// plugin has no public API: the host reaches it through the assembly-level entry-point attribute.
[assembly: InternalsVisibleTo("Dongled.Plugins.Tests")]
