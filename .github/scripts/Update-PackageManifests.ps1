#Requires -Version 7.4
<#
.SYNOPSIS
   Points the Scoop manifest (bucket/gclo.json) and the Chocolatey package
   (packaging/chocolatey) at a stable release's CLI zip.

.DESCRIPTION
   Both package managers install the same asset, gclo-cli-win-x64.zip, by URL and
   SHA-256. This script rewrites the version, URL, checksum, and release-notes link
   in all three files from one source of truth: the version and the zip's hash
   (computed from -CliZipPath, or taken from -Sha256). The release workflow runs it
   on every stable tag before `choco pack` and opens a PR with the result so the
   committed manifests always describe the latest stable release.

.PARAMETER Version
   The stable semantic version, e.g. 1.2.3 (no leading 'v').

.PARAMETER CliZipPath
   Path to the built gclo-cli-win-x64.zip to hash. Either this or -Sha256.

.PARAMETER Sha256
   The zip's SHA-256 (lowercase hex) when it is already known.
#>
[CmdletBinding()]
param(
   [Parameter(Mandatory)]
   [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
   [string] $Version,

   [string] $CliZipPath,

   [string] $Sha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<#
.SYNOPSIS
   Overwrites a text file keeping its encoding (BOM or not) as it was.
#>
function Write-FileText
{
   param(
      [Parameter(Mandatory)] [string] $Path,
      [Parameter(Mandatory)] [string] $Text
   )

   $bytes = [System.IO.File]::ReadAllBytes($Path)
   $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
   [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($hasBom))
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

if(-not $Sha256)
{
   if(-not $CliZipPath)
   {
      throw 'Give -CliZipPath or -Sha256.'
   }
   $Sha256 = (Get-FileHash -LiteralPath $CliZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
if($Sha256 -notmatch '^[0-9a-f]{64}$')
{
   throw "'$Sha256' is not a lowercase SHA-256."
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$url = "https://github.com/KofTwentyTwo/gclo/releases/download/v$Version/gclo-cli-win-x64.zip"

################################################################################
## Scoop: a real JSON edit, so the file stays valid whatever its layout.      ##
################################################################################
$scoopPath = Join-Path $repoRoot 'bucket\gclo.json'
$scoop = Get-Content -LiteralPath $scoopPath -Raw | ConvertFrom-Json
$scoop.version = $Version
$scoop.architecture.'64bit'.url = $url
$scoop.architecture.'64bit'.hash = $Sha256
$scoopJson = ($scoop | ConvertTo-Json -Depth 10) -replace '\\u0026', '&'
Write-FileText -Path $scoopPath -Text ($scoopJson + "`n")
Write-Progress-Line "Scoop manifest -> $Version"

################################################################################
## Chocolatey: nuspec version + release notes link, install script URL + hash. ##
################################################################################
$nuspecPath = Join-Path $repoRoot 'packaging\chocolatey\gclo.nuspec'
$nuspec = Get-Content -LiteralPath $nuspecPath -Raw
$nuspec = $nuspec -replace '<version>[^<]+</version>', "<version>$Version</version>"
$nuspec = $nuspec -replace '<releaseNotes>[^<]+</releaseNotes>', "<releaseNotes>https://github.com/KofTwentyTwo/gclo/releases/tag/v$Version</releaseNotes>"
Write-FileText -Path $nuspecPath -Text $nuspec

$installPath = Join-Path $repoRoot 'packaging\chocolatey\tools\chocolateyinstall.ps1'
$install = Get-Content -LiteralPath $installPath -Raw
$install = $install -replace "url64bit\s*=\s*'[^']+'", "url64bit = '$url'"
$install = $install -replace "checksum64\s*=\s*'[^']+'", "checksum64 = '$Sha256'"
Write-FileText -Path $installPath -Text $install
Write-Progress-Line "Chocolatey package -> $Version"

if($env:GITHUB_OUTPUT)
{
   Add-Content -Path $env:GITHUB_OUTPUT -Value "sha256=$Sha256"
}
