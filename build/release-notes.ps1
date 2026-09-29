<#
.SYNOPSIS
    Writes a release's notes from the commits since the previous release.

.DESCRIPTION
    Commit subjects are read as conventional commits ("type(scope)!: description") and grouped:
    breaking changes, features, fixes, then everything else. A subject that is not a conventional
    commit is listed under "Other changes" as written. Merge commits are left out.

    The notes also say which download to pick, and list the plugins shipped with their versions,
    marking the ones that changed since the previous release.

    Works in Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER Version
    This release's version.

.PARAMETER PreviousTag
    The previous release's tag. Omit it for the first release, which lists every commit.

.PARAMETER PluginsJson
    This release's plugins.json.

.PARAMETER PreviousPluginsJson
    The previous release's plugins.json, if there is one.

.PARAMETER OutFile
    Where to write the notes (Markdown).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $PreviousTag,
    [Parameter(Mandatory)] [string] $PluginsJson,
    [string] $PreviousPluginsJson,
    [Parameter(Mandatory)] [string] $OutFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$range = if ($PreviousTag) { "$PreviousTag..HEAD" } else { 'HEAD' }

# One record per commit: hash, subject and body, separated by control characters no commit message
# contains.
$raw = git log --no-merges "--format=%h%x1f%s%x1f%b%x1e" $range
if ($LASTEXITCODE -ne 0) {
    throw "git log $range failed. The release job needs the full history and tags (fetch-depth: 0)."
}

$groups = [ordered]@{
    'Breaking changes' = New-Object System.Collections.Generic.List[string]
    'Features'         = New-Object System.Collections.Generic.List[string]
    'Fixes'            = New-Object System.Collections.Generic.List[string]
    'Performance'      = New-Object System.Collections.Generic.List[string]
    'Other changes'    = New-Object System.Collections.Generic.List[string]
}

$pattern = '^(?<type>[a-zA-Z]+)(\((?<scope>[^)]*)\))?(?<bang>!)?:\s*(?<description>.+)$'

foreach ($record in (($raw -join "`n") -split [char]0x1e)) {
    $record = $record.Trim()
    if (-not $record) { continue }

    $fields = $record -split [char]0x1f
    $hash = $fields[0].Trim()
    $subject = $fields[1].Trim()
    $body = if ($fields.Count -gt 2) { $fields[2] } else { '' }

    $match = [regex]::Match($subject, $pattern)
    if (-not $match.Success) {
        $groups['Other changes'].Add("- $subject ($hash)")
        continue
    }

    $type = $match.Groups['type'].Value.ToLowerInvariant()
    $scope = $match.Groups['scope'].Value
    $description = $match.Groups['description'].Value
    $line = if ($scope) { "- **$scope**: $description ($hash)" } else { "- $description ($hash)" }

    if ($match.Groups['bang'].Success -or $body -match '(?m)^BREAKING[ -]CHANGE:') {
        $groups['Breaking changes'].Add($line)
    } elseif ($type -eq 'feat') {
        $groups['Features'].Add($line)
    } elseif ($type -eq 'fix') {
        $groups['Fixes'].Add($line)
    } elseif ($type -eq 'perf') {
        $groups['Performance'].Add($line)
    } else {
        $groups['Other changes'].Add($line)
    }
}

$notes = New-Object System.Text.StringBuilder

if ($PreviousTag) {
    [void]$notes.AppendLine("Changes since $PreviousTag.")
} else {
    [void]$notes.AppendLine('The first release.')
}
[void]$notes.AppendLine()

$any = $false
foreach ($name in $groups.Keys) {
    $lines = $groups[$name]
    if ($lines.Count -eq 0) { continue }
    $any = $true
    [void]$notes.AppendLine("## $name")
    [void]$notes.AppendLine()
    foreach ($line in $lines) { [void]$notes.AppendLine($line) }
    [void]$notes.AppendLine()
}
if (-not $any) {
    [void]$notes.AppendLine('No changes other than merges.')
    [void]$notes.AppendLine()
}

[void]$notes.AppendLine('## Downloads')
[void]$notes.AppendLine()
[void]$notes.AppendLine('Windows 10 1809 or later, x64. Unzip anywhere you can write to and run `Dongled.exe`.')
[void]$notes.AppendLine()
[void]$notes.AppendLine("- ``Dongled-$Version-win-x64.zip``: everything included. Pick this one if unsure.")
[void]$notes.AppendLine("- ``Dongled-$Version-win-x64-framework-dependent.zip``: smaller, and needs the [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) installed.")
[void]$notes.AppendLine('- `SHA256SUMS`: checksums of both zips.')
[void]$notes.AppendLine()

$current = (Get-Content -Raw $PluginsJson | ConvertFrom-Json).plugins
$previous = @()
if ($PreviousPluginsJson) {
    $previous = @((Get-Content -Raw $PreviousPluginsJson | ConvertFrom-Json).plugins)
}

[void]$notes.AppendLine('## Plugins')
[void]$notes.AppendLine()
[void]$notes.AppendLine('Shipped in `Plugins\`, and trusted without an approval while their files are unchanged.')
[void]$notes.AppendLine()
[void]$notes.AppendLine('| Plugin | Version | |')
[void]$notes.AppendLine('| --- | --- | --- |')
foreach ($plugin in $current) {
    $old = $previous | Where-Object { $_.name -eq $plugin.name } | Select-Object -First 1
    $change = if (-not $PreviousPluginsJson) { '' }
        elseif (-not $old) { 'new' }
        elseif ($old.sha256 -ne $plugin.sha256) { "updated from $($old.version)" }
        else { 'unchanged' }
    [void]$notes.AppendLine("| $($plugin.name) | $($plugin.version) | $change |")
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutFile), $notes.ToString().Replace("`r`n", "`n"), $utf8)
Write-Host "Wrote $OutFile."
