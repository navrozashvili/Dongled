# InspectAssembly

Throwaway diagnostic that dumps the types and plugin-interface implementations in a given
assembly; used when investigating why a plugin failed to load. Not part of the shipped app
and deliberately excluded from the solution's default build.

```
dotnet run --project tools/InspectAssembly -- <path-to-assembly.dll>
```
