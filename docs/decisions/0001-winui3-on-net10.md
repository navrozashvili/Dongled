# 1. WinUI 3 on net10, unpackaged, with a tray icon

**Date:** 2026-07-25
**Status:** Accepted
**Context:** Choosing the UI stack, and proving it could host a notification-area icon, before
anything else was built on it.

## Decision

WinUI 3 / Windows App SDK, unpackaged (`WindowsPackageType=None`) and self-contained
(`WindowsAppSDKSelfContained=true`), on `net10.0-windows10.0.19041.0`.

- `Microsoft.WindowsAppSDK` **2.3.1**
- Tray icon: **H.NotifyIcon.WinUI 2.4.1**

## Why self-contained

Releases ship as a self-contained zip, so there is no Windows App SDK runtime for a user to
install and no runtime version for a developer to match. The build output carries
`Microsoft.WindowsAppRuntime.dll`, `Microsoft.WinUI.dll` and the XAML resources next to the exe,
and `Dongled.App.runtimeconfig.json` declares `includedFrameworks`, so the app runs on a machine
with no matching WindowsAppRuntime installed.

## Why RuntimeIdentifier instead of Platforms

Declaring `<Platforms>x64;ARM64</Platforms>` invalidates MSBuild's default `AnyCPU` and
forces `-p:Platform=x64` onto every build and test command. Setting `RuntimeIdentifier`
alone keeps plain `dotnet build` working.

## Package compatibility

`H.NotifyIcon.WinUI` 2.4.1 declares `Microsoft.WindowsAppSDK >= 1.6`, which NuGet satisfies with
the pinned 2.3.1. Its assembly already references `WinRT.Runtime 2.2.0.0`, i.e. it is built for
the 2.x-era projections despite the older floor in its metadata.

`Microsoft.Windows.SDK.BuildTools` arrives transitively via `Microsoft.WindowsAppSDK.Base`, so no
Windows Kits installation is required.

Two details that are easy to get wrong:

- `H.NotifyIcon.TaskbarIcon.IconSource` is typed `Microsoft.UI.Xaml.Media.ImageSource`, not
  `Microsoft.UI.Xaml.Controls.IconSource`. Assigning a `BitmapIconSource` fails with
  `WMC0015`; use a `BitmapImage`. `ms-appx:///` URIs resolve in unpackaged mode.
- A `WMC9999: Object reference not set` from the XAML compiler, with `WMC1509: No LocalAssembly
  parameter given during MarkupCompilePass2`, is a symptom of `CoreCompile` failing first. Fix
  the C# error and it goes away.

## Analyzer suppressions

`AnalysisLevel=latest-all` plus `TreatWarningsAsErrors` needs two narrowly scoped suppressions in
`src/Dongled.App/GlobalSuppressions.cs`:

- **CA5392** fires inside `UndockedRegFreeWinRT-AutoInitializer.cs`, which
  `Microsoft.WindowsAppSDK.Foundation` injects into the compilation from the NuGet package. It is
  generated, lives outside the repo so `.editorconfig` cannot reach it, and the SDK has no
  opt-out. It is suppressed for that one member only, so CA5392 stays active for this repo's own
  P/Invokes.
- **CA1515** ("types can be made internal") is suppressed per type for the types the test project
  must see, rather than project-wide, so a type that becomes public by accident is still reported.

## Consequences

- The tray icon needs a package (or hand-written `Shell_NotifyIcon` P/Invoke), and
  single-instance activation is wired manually because unpackaged apps do not get it for free.
- `H.NotifyIcon.WinUI`'s declared WindowsAppSDK floor (1.6) understates what it supports; do not
  let the version metadata alone drive a future downgrade.
- The tray icon's bitmap cannot be verified from XAML. `H.NotifyIcon` reads only
  `BitmapImage.UriSource` and loads the ICO through Win32 itself; the image never enters the
  visual tree, so `PixelWidth` stays `0` and neither `ImageOpened` nor `ImageFailed` fires.
  Confirming the bitmap is a human check, or a direct WIC decode of the asset.
- GDI+ (`System.Drawing.Icon.ToBitmap()`) cannot read PNG-compressed ICO entries. That is
  expected and does not mean the asset is broken; WIC decodes it.

## Referencing the app from a test project

`Dongled.App` is a self-contained executable, and xunit v3 test projects are
Microsoft.Testing.Platform executables. The SDK refuses that reference:

```
error NETSDK1151: The referenced project '..\..\src\Dongled.App\Dongled.App.csproj'
is a self-contained executable.  A self-contained executable cannot be referenced by a
non self-contained executable.
```

`Microsoft.NET.Sdk.targets` waives the check only for VSTest-style projects
(`IsTestProject=true` **and** `IsTestingPlatformApplication != true`), which an MTP project is
not. `SelfContained=true` on the app is not negotiable, so the fix lives on the test project:
`tests/Dongled.App.Tests/Dongled.App.Tests.csproj` sets
`ValidateExecutableReferencesMatchSelfContained=false`, alongside `SelfContained=false`,
`WindowsAppSDKSelfContained=false`, `RuntimeIdentifier=win-x64`, and `UseWinUI=false`. Do not
remove that property.

With that, view models can live in `Dongled.App` itself; no separate view-model project is
needed. Tests must stay reflection-only over WinUI types: the test host has no XAML runtime, so
constructing `App` or `MainWindow` is out of scope.
