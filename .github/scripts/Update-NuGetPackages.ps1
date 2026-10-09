#Requires -Version 7.4
<#
.SYNOPSIS
   Bumps every centrally managed NuGet package to its latest stable version and writes
   a Markdown report of what changed and what was held back.

.DESCRIPTION
   Replaces Dependabot's NuGet ecosystem for this repository (docs/adr/0004).
   Dependabot's NuGet updater mis-evaluates Windows-versioned target frameworks such
   as net10.0-windows10.0.19041.0 (dependabot/dependabot-core#13923) and declares
   every newer package "not compatible". `dotnet list package --outdated` uses the
   real NuGet/MSBuild evaluation, so this script drives the weekly update PR from it.

   Policy, mirroring the Dependabot config it replaces (K22-DEP-20):
      - minor and patch bumps are applied;
      - major bumps are reported but NOT applied unless -IncludeMajor is given;
      - prerelease versions are never applied to a package on a stable one;
      - a version published fewer than -CooldownDays days ago is held back.

   Versions live in Directory.Packages.props (central package management) and are
   edited in place there, preserving the file's encoding and line endings.

.PARAMETER Solution
   Solution to evaluate. Default: gclo.slnx.

.PARAMETER PackagesProps
   The central package file to edit. Default: Directory.Packages.props.

.PARAMETER ReportPath
   Where to write the Markdown report. Default: nuget-update-report.md.

.PARAMETER IncludeMajor
   Also apply major-version bumps.

.PARAMETER CooldownDays
   Hold back versions published fewer than this many days ago. Default: 7. When
   nuget.org cannot be asked for the publish date, the version is held.

.OUTPUTS
   Writes `changed=true|false` and `report=<path>` to $env:GITHUB_OUTPUT when set.
   Exit code 0 on success (whether or not anything changed), non-zero on error.
#>
[CmdletBinding()]
param(
   [ValidateNotNullOrEmpty()]
   [string] $Solution = 'gclo.slnx',

   [ValidateNotNullOrEmpty()]
   [string] $PackagesProps = 'Directory.Packages.props',

   [ValidateNotNullOrEmpty()]
   [string] $ReportPath = 'nuget-update-report.md',

   [switch] $IncludeMajor,

   [ValidateRange(0, 365)]
   [int] $CooldownDays = 7
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

################################################################################
## [System.IO.File] resolves relative paths against the process working      ##
## directory, which is not necessarily PowerShell's location; anchor them.   ##
################################################################################
$ReportPath = [System.IO.Path]::GetFullPath($ReportPath, $PWD.Path)
$PackagesProps = [System.IO.Path]::GetFullPath($PackagesProps, $PWD.Path)

<#
.SYNOPSIS
   Reads a property of a parsed JSON object, or $null when the object does not have
   it; `dotnet list` omits members that would be empty.
#>
function Get-PropertyValue
{
   param(
      [Parameter(Mandatory)] [object] $Object,
      [Parameter(Mandatory)] [string] $Name
   )

   $property = $Object.PSObject.Properties[$Name]
   if($null -eq $property)
   {
      return $null
   }
   return $property.Value
}

<#
.SYNOPSIS
   The numeric core of a NuGet version. Versions may carry a prerelease suffix
   (1.2.3-beta.1) and may have four numeric parts (10.0.28000.2705); [version]
   handles the latter but not the former.
#>
function Get-NumericVersion
{
   param([Parameter(Mandatory)] [string] $Version)

   $core = ($Version -split '[-+]', 2)[0]
   return [version] $core
}

<#
.SYNOPSIS
   True when a NuGet version string carries a prerelease suffix.
#>
function Test-Prerelease
{
   param([Parameter(Mandatory)] [string] $Version)

   return $Version.Contains('-')
}

<#
.SYNOPSIS
   The publish timestamp of one package version from nuget.org's registration leaf,
   or $null when it cannot be read (the caller then holds the version back).
#>
function Get-PublishedDate
{
   param(
      [Parameter(Mandatory)] [string] $Id,
      [Parameter(Mandatory)] [string] $Version
   )

   $url = "https://api.nuget.org/v3/registration5-gz-semver2/$($Id.ToLowerInvariant())/$($Version.ToLowerInvariant()).json"
   try
   {
      $leaf = Invoke-RestMethod -Uri $url -TimeoutSec 30
      return [DateTimeOffset]::Parse($leaf.published, [System.Globalization.CultureInfo]::InvariantCulture)
   }
   catch
   {
      return $null
   }
}

<#
.SYNOPSIS
   Writes a progress line to the information stream (visible in the workflow log).
#>
function Write-Progress-Line
{
   param([Parameter(Mandatory)] [string] $Message)

   Write-Information -MessageData $Message -InformationAction Continue
}

Write-Progress-Line "Restoring $Solution"
dotnet restore $Solution | Out-Null
if($LASTEXITCODE -ne 0)
{
   throw "dotnet restore failed with exit code $LASTEXITCODE."
}

Write-Progress-Line 'Querying outdated packages'
$raw = dotnet list $Solution package --outdated --format json
if($LASTEXITCODE -ne 0)
{
   throw "dotnet list package --outdated failed with exit code $LASTEXITCODE."
}
$listing = ($raw -join "`n") | ConvertFrom-Json

################################################################################
## One row per package: versions are central, so a package listed under      ##
## several projects or frameworks is still one edit.                          ##
################################################################################
$rows = @{}
foreach($project in @((Get-PropertyValue $listing 'projects') | Where-Object { $_ }))
{
   foreach($framework in @((Get-PropertyValue $project 'frameworks') | Where-Object { $_ }))
   {
      foreach($package in @((Get-PropertyValue $framework 'topLevelPackages') | Where-Object { $_ }))
      {
         if(-not $rows.ContainsKey($package.id))
         {
            $rows[$package.id] = [pscustomobject]@{ Id = $package.id; Requested = $package.requestedVersion; Latest = $package.latestVersion }
         }
      }
   }
}

$applied = [System.Collections.Generic.List[object]]::new()
$held = [System.Collections.Generic.List[object]]::new()

$bytes = [System.IO.File]::ReadAllBytes($PackagesProps)
$hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
$text = [System.Text.Encoding]::UTF8.GetString($bytes)
if($hasBom)
{
   $text = $text.Substring(1)
}
$original = $text

foreach($row in ($rows.Values | Sort-Object Id))
{
   $current = Get-NumericVersion -Version $row.Requested
   $latest = Get-NumericVersion -Version $row.Latest

   $reason = $null
   if((Test-Prerelease -Version $row.Latest) -and -not (Test-Prerelease -Version $row.Requested))
   {
      $reason = 'latest is a prerelease'
   }
   elseif($latest.Major -ne $current.Major -and -not $IncludeMajor)
   {
      $reason = 'major version bump: review manually (or run with -IncludeMajor)'
   }
   elseif($latest -le $current)
   {
      $reason = 'not newer than the requested version'
   }
   elseif($CooldownDays -gt 0)
   {
      $published = Get-PublishedDate -Id $row.Id -Version $row.Latest
      if($null -eq $published)
      {
         $reason = "publish date unavailable from nuget.org; held for the $CooldownDays-day cooldown"
      }
      elseif($published -gt [DateTimeOffset]::UtcNow.AddDays(-$CooldownDays))
      {
         $age = [int] [math]::Floor(([DateTimeOffset]::UtcNow - $published).TotalDays)
         $reason = "published $age day(s) ago; waits out the $CooldownDays-day cooldown"
      }
   }

   if($reason)
   {
      $held.Add([pscustomobject]@{ Id = $row.Id; From = $row.Requested; To = $row.Latest; Reason = $reason })
      continue
   }

   ################################################################################
   ## Exact textual edit of the one PackageVersion, so nothing else moves.       ##
   ################################################################################
   $pattern = '(<PackageVersion\s+Include="' + [regex]::Escape($row.Id) + '"\s+Version=")' + [regex]::Escape($row.Requested) + '(")'
   $updated = [regex]::Replace($text, $pattern, ('${1}' + $row.Latest + '${2}'), [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
   if($updated -eq $text)
   {
      $held.Add([pscustomobject]@{ Id = $row.Id; From = $row.Requested; To = $row.Latest; Reason = "PackageVersion not found in $([System.IO.Path]::GetFileName($PackagesProps))" })
      continue
   }

   $text = $updated
   $applied.Add([pscustomobject]@{ Id = $row.Id; From = $row.Requested; To = $row.Latest })
   Write-Progress-Line "  $($row.Id) $($row.Requested) -> $($row.Latest)"
}

if($text -ne $original)
{
   [System.IO.File]::WriteAllText($PackagesProps, $text, [System.Text.UTF8Encoding]::new($hasBom))
}

################################################################################
## Report                                                                     ##
################################################################################
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('## NuGet package updates')
$lines.Add('')
if($applied.Count -eq 0)
{
   $lines.Add('No minor or patch updates were available.')
}
else
{
   $lines.Add('| Package | From | To |')
   $lines.Add('| --- | --- | --- |')
   foreach($a in $applied)
   {
      $lines.Add("| $($a.Id) | $($a.From) | $($a.To) |")
   }
}
if($held.Count -gt 0)
{
   $lines.Add('')
   $lines.Add('### Held back (not applied)')
   $lines.Add('')
   $lines.Add('| Package | Current | Latest | Why |')
   $lines.Add('| --- | --- | --- | --- |')
   foreach($h in $held)
   {
      $lines.Add("| $($h.Id) | $($h.From) | $($h.To) | $($h.Reason) |")
   }
}
$lines.Add('')
$lines.Add("_Generated by ``.github/scripts/Update-NuGetPackages.ps1`` from ``dotnet list package --outdated`` on $(Get-Date -Format 'yyyy-MM-dd')._")
[System.IO.File]::WriteAllText($ReportPath, ($lines -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))

Write-Progress-Line ''
Write-Progress-Line (Get-Content $ReportPath -Raw)

$changed = $applied.Count -gt 0
if($env:GITHUB_OUTPUT)
{
   Add-Content -Path $env:GITHUB_OUTPUT -Value "changed=$($changed.ToString().ToLowerInvariant())"
   Add-Content -Path $env:GITHUB_OUTPUT -Value "report=$ReportPath"
}
