<#
.SYNOPSIS
    Bumps every top-level PackageReference in the solution to its latest stable
    version and writes a Markdown report of what changed and what was held back.

.DESCRIPTION
    Replaces Dependabot's NuGet ecosystem for this repository. Dependabot's NuGet
    updater mis-evaluates Windows-versioned target frameworks such as
    net10.0-windows10.0.19041.0 (dependabot/dependabot-core#13923): it declares every
    newer package "not compatible" with the WinUI project and, because it updates a
    package consistently across all projects, that blocks the test projects too.
    `dotnet list package --outdated` uses the real NuGet/MSBuild evaluation and has no
    such problem, so this script drives the weekly update PR from it instead.

    Policy, mirroring the Dependabot config it replaces:
      - minor and patch bumps are applied;
      - major bumps are reported but NOT applied unless -IncludeMajor is given, so
        they get a deliberate review;
      - prerelease versions are never applied to a package that is on a stable one.

    Versions are edited in place in each .csproj (the exact Include/Version pair),
    preserving the file's encoding and line endings.

.PARAMETER Solution
    Solution to evaluate. Default: gclo.slnx.

.PARAMETER ReportPath
    Where to write the Markdown report. Default: nuget-update-report.md.

.PARAMETER IncludeMajor
    Also apply major-version bumps.

.OUTPUTS
    Writes `changed=true|false` and `report=<path>` to $env:GITHUB_OUTPUT when set.
    Exit code 0 on success (whether or not anything changed), non-zero on error.
#>
[CmdletBinding()]
param(
    [string]$Solution = 'gclo.slnx',
    [string]$ReportPath = 'nuget-update-report.md',
    [switch]$IncludeMajor
)

$ErrorActionPreference = 'Stop'

# [System.IO.File] resolves relative paths against the process working directory,
# which is not necessarily PowerShell's current location; anchor it explicitly.
$ReportPath = [System.IO.Path]::GetFullPath($ReportPath, (Get-Location).Path)

function Get-NumericVersion([string]$version) {
    # NuGet versions may carry a prerelease suffix (1.2.3-beta.1) and may have four
    # numeric parts (10.0.28000.2705); [version] handles the latter but not the former.
    $core = ($version -split '[-+]', 2)[0]
    return [version]$core
}

function Test-Prerelease([string]$version) {
    return $version.Contains('-')
}

Write-Host "Restoring $Solution"
dotnet restore $Solution | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }

Write-Host "Querying outdated packages"
$raw = dotnet list $Solution package --outdated --format json
if ($LASTEXITCODE -ne 0) { throw "dotnet list package --outdated failed with exit code $LASTEXITCODE." }
$listing = ($raw -join "`n") | ConvertFrom-Json

# One row per (project, package): the same package can be listed under several
# target frameworks of one project, and we edit the csproj once.
$rows = @{}
# Projects with nothing outdated have no 'frameworks' member at all, and a framework
# with nothing outdated has no 'topLevelPackages'; @($null) still yields one $null
# iteration, hence the Where-Object filters.
foreach ($project in @($listing.projects | Where-Object { $_ })) {
    foreach ($framework in @($project.frameworks | Where-Object { $_ })) {
        foreach ($package in @($framework.topLevelPackages | Where-Object { $_ })) {
            $key = "$($project.path)|$($package.id)"
            if (-not $rows.ContainsKey($key)) {
                $rows[$key] = [pscustomobject]@{
                    Project   = $project.path
                    Id        = $package.id
                    Requested = $package.requestedVersion
                    Latest    = $package.latestVersion
                }
            }
        }
    }
}

$applied = New-Object System.Collections.Generic.List[object]
$held = New-Object System.Collections.Generic.List[object]

foreach ($row in ($rows.Values | Sort-Object Project, Id)) {
    $current = Get-NumericVersion $row.Requested
    $latest = Get-NumericVersion $row.Latest
    $projectName = [System.IO.Path]::GetFileName($row.Project)

    $reason = $null
    if ((Test-Prerelease $row.Latest) -and -not (Test-Prerelease $row.Requested)) {
        $reason = 'latest is a prerelease'
    }
    elseif ($latest.Major -ne $current.Major -and -not $IncludeMajor) {
        $reason = 'major version bump: review manually (or run with -IncludeMajor)'
    }
    elseif ($latest -le $current) {
        $reason = 'not newer than the requested version'
    }

    if ($reason) {
        $held.Add([pscustomobject]@{ Project = $projectName; Id = $row.Id; From = $row.Requested; To = $row.Latest; Reason = $reason })
        continue
    }

    # Exact textual edit of the one PackageReference, so nothing else in the file moves.
    $bytes = [System.IO.File]::ReadAllBytes($row.Project)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    if ($hasBom) { $text = $text.Substring(1) }

    $pattern = '(<PackageReference\s+Include="' + [regex]::Escape($row.Id) + '"\s+Version=")' + [regex]::Escape($row.Requested) + '(")'
    $updated = [regex]::Replace($text, $pattern, ('${1}' + $row.Latest + '${2}'), [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($updated -eq $text) {
        $held.Add([pscustomobject]@{ Project = $projectName; Id = $row.Id; From = $row.Requested; To = $row.Latest; Reason = 'PackageReference not found in the project file (central/variable version?)' })
        continue
    }

    [System.IO.File]::WriteAllText($row.Project, $updated, (New-Object System.Text.UTF8Encoding($hasBom)))
    $applied.Add([pscustomobject]@{ Project = $projectName; Id = $row.Id; From = $row.Requested; To = $row.Latest })
    Write-Host "  $projectName : $($row.Id) $($row.Requested) -> $($row.Latest)"
}

# ---------------------------------------------------------------- report
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('## NuGet package updates')
$lines.Add('')
if ($applied.Count -eq 0) {
    $lines.Add('No minor or patch updates were available.')
}
else {
    $lines.Add('| Project | Package | From | To |')
    $lines.Add('| --- | --- | --- | --- |')
    foreach ($a in $applied) { $lines.Add("| $($a.Project) | $($a.Id) | $($a.From) | $($a.To) |") }
}
if ($held.Count -gt 0) {
    $lines.Add('')
    $lines.Add('### Held back (not applied)')
    $lines.Add('')
    $lines.Add('| Project | Package | Current | Latest | Why |')
    $lines.Add('| --- | --- | --- | --- | --- |')
    foreach ($h in $held) { $lines.Add("| $($h.Project) | $($h.Id) | $($h.From) | $($h.To) | $($h.Reason) |") }
}
$lines.Add('')
$lines.Add("_Generated by ``.github/scripts/Update-NuGetPackages.ps1`` from ``dotnet list package --outdated`` on $(Get-Date -Format 'yyyy-MM-dd')._")
[System.IO.File]::WriteAllText($ReportPath, ($lines -join "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))

Write-Host ''
Write-Host (Get-Content $ReportPath -Raw)

$changed = $applied.Count -gt 0
if ($env:GITHUB_OUTPUT) {
    Add-Content -Path $env:GITHUB_OUTPUT -Value "changed=$($changed.ToString().ToLowerInvariant())"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "report=$ReportPath"
}
