<#
.SYNOPSIS
    Builds a Dongled release: both zips, SHA256SUMS and plugins.json.

.DESCRIPTION
    Runs the same steps locally as the release workflow does on GitHub.

      1. Publishes every plugin under plugins/ into Plugins\<Name>\ (Name is the project name without
         "Dongled.Plugin."), with no per-build values: plugins build to the same bytes at every commit
         where their source is unchanged.
      2. Hashes each plugin folder with PluginManifest (through tools/Dongled.ReleaseTool), checks
         each against the previous release's plugins.json, and writes this release's plugins.json.
         A plugin whose files changed while its <Version> did not fails the build, and so does one
         whose version changed while its files did not.
      3. Publishes the app twice, self-contained and framework-dependent (the Windows App SDK is
         self-contained in both), with plugins.json compiled into Dongled.Core.dll as the list of
         plugins trusted without an approval.
      4. Copies the same Plugins\ folder into both, zips each with the app files at the zip root,
         and writes SHA256SUMS over both zips.

    Works in Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER Version
    The app version, major.minor.patch. Only with -Official; every other build is 0.0.0-dev.

.PARAMETER Official
    Marks the app as an official build (AssemblyMetadata DongledOfficialBuild=true). Only the
    release workflow should pass this for a build it publishes.

.PARAMETER OutputDirectory
    Where the zips, SHA256SUMS and plugins.json are written, with intermediate files under
    OutputDirectory\work. Emptied first. Defaults to artifacts\release in the repository.

.PARAMETER PreviousPluginsJson
    The previous release's plugins.json. Omit it only when there is no previous release.

.EXAMPLE
    ./build/release.ps1 -OutputDirectory $env:TEMP\dongled-release

.EXAMPLE
    ./build/release.ps1 -Version 1.0.42 -Official -PreviousPluginsJson .\previous\plugins.json
#>
[CmdletBinding()]
param(
    [string] $Version = '0.0.0-dev',
    [switch] $Official,
    [string] $OutputDirectory,
    [string] $PreviousPluginsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot

function Invoke-Native {
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [Parameter(Mandatory)] [string[]] $Arguments
    )

    Write-Host "> $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'$FilePath' failed with exit code $LASTEXITCODE."
    }
}

# Identity. A real version only with -Official, so that nothing but the release workflow produces a
# build that claims to be a release.
if ($Official) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') {
        throw "An official build needs a major.minor.patch version, not '$Version'."
    }
} elseif ($Version -ne '0.0.0-dev') {
    throw "Only an official build (-Official) takes a version. Every other build is 0.0.0-dev."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repo 'artifacts\release'
}
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)

if ($PreviousPluginsJson) {
    $PreviousPluginsJson = (Resolve-Path $PreviousPluginsJson).Path
}

if (Test-Path $OutputDirectory) {
    Remove-Item -Recurse -Force $OutputDirectory
}

$work = Join-Path $OutputDirectory 'work'
$stagedPlugins = Join-Path $work 'Plugins'
New-Item -ItemType Directory -Force $stagedPlugins | Out-Null

# Every build goes through its own artifacts path, so nothing is written into a project's bin\ or
# obj\ (where a developer's running copy may live), and the two app flavors never share
# intermediate files.
$common = @('-c', 'Release', '-p:ContinuousIntegrationBuild=true', '-p:RestoreLockedMode=true', '-nologo')

# 1. The release tool.
$toolArtifacts = Join-Path $work 'obj-tool'
Invoke-Native dotnet (@('build', (Join-Path $repo 'tools\Dongled.ReleaseTool\Dongled.ReleaseTool.csproj')) + $common + @(
    '--artifacts-path', $toolArtifacts))
$tool = Join-Path $toolArtifacts 'bin\Dongled.ReleaseTool\release\Dongled.ReleaseTool.exe'

# 2. Plugins. Deliberately given no version or official-build property: a plugin carries its own
#    <Version> and nothing about this particular build.
$pluginProjects = Get-ChildItem (Join-Path $repo 'plugins') -Directory -Filter 'Dongled.Plugin.*' |
    Sort-Object Name |
    ForEach-Object { Join-Path $_.FullName "$($_.Name).csproj" }

foreach ($project in $pluginProjects) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($project).Substring('Dongled.Plugin.'.Length)
    Invoke-Native dotnet (@('publish', $project) + $common + @(
        '--artifacts-path', (Join-Path $work 'obj-plugins'),
        '-o', (Join-Path $stagedPlugins $name)))
}

$pluginsJson = Join-Path $OutputDirectory 'plugins.json'
$pluginArguments = @('plugins', '--root', $stagedPlugins, '--out', $pluginsJson)
if ($PreviousPluginsJson) {
    $pluginArguments += @('--previous', $PreviousPluginsJson)
}
Invoke-Native $tool $pluginArguments

# 3 and 4. The app, per flavor.
$flavors = @(
    @{ Name = 'self-contained'; SelfContained = 'true'; Zip = "Dongled-$Version-win-x64.zip" },
    @{ Name = 'framework-dependent'; SelfContained = 'false'; Zip = "Dongled-$Version-win-x64-framework-dependent.zip" }
)

$zips = @()
foreach ($flavor in $flavors) {
    $appOut = Join-Path $work "app-$($flavor.Name)"

    $officialBuild = if ($Official) { 'true' } else { 'false' }
    Invoke-Native dotnet (@('publish', (Join-Path $repo 'src\Dongled.App\Dongled.App.csproj')) + $common + @(
        # No -r: the project already sets RuntimeIdentifier=win-x64, and a -r here would become a
        # global property that reaches the libraries' locked restore and fails it (NU1004).
        "-p:SelfContained=$($flavor.SelfContained)",
        '--artifacts-path', (Join-Path $work "obj-app-$($flavor.Name)"),
        '-o', $appOut,
        "-p:DongledVersion=$Version",
        "-p:DongledOfficialBuild=$officialBuild",
        "-p:DongledBundledPlugins=$pluginsJson"))

    $appPlugins = Join-Path $appOut 'Plugins'
    if (Test-Path $appPlugins) {
        throw "The app's publish output already has a Plugins folder at $appPlugins; the release assembles it."
    }
    Copy-Item -Recurse $stagedPlugins $appPlugins

    $zip = Join-Path $OutputDirectory $flavor.Zip
    Invoke-Native $tool @('zip', '--source', $appOut, '--out', $zip)
    $zips += $zip
}

Invoke-Native $tool (@('sums', '--out', (Join-Path $OutputDirectory 'SHA256SUMS')) + $zips)

Write-Host ''
Write-Host "Release $Version ($(if ($Official) { 'official' } else { 'not official' })) is in $OutputDirectory"
Get-ChildItem $OutputDirectory -File | ForEach-Object { Write-Host ("  {0,-60} {1,12:N0} bytes" -f $_.Name, $_.Length) }
