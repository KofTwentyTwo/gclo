<#
.SYNOPSIS
    Points the Scoop manifest (bucket/gclo.json) and the Chocolatey package
    (packaging/chocolatey) at a stable release's CLI zip.

.DESCRIPTION
    Both package managers install the same asset, gclo-cli-win-x64.zip, by URL and
    SHA-256. This script rewrites the version, URL, checksum, and release-notes
    link in all three files from one source of truth: the version and the zip's
    hash (computed from -CliZipPath, or taken from -Sha256). The release workflow
    runs it on every stable tag before `choco pack` and opens a PR with the result
    so the committed manifests always describe the latest stable release.

.PARAMETER Version
    The stable semantic version, e.g. 1.2.3 (no leading 'v').

.PARAMETER CliZipPath
    Path to the built gclo-cli-win-x64.zip to hash. Either this or -Sha256.

.PARAMETER Sha256
    The zip's SHA-256 (lowercase hex) when it is already known.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Version,
    [string]$CliZipPath,
    [string]$Sha256
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw "Version '$Version' is not a stable semantic version (prerelease builds are not published to package managers)."
}
if (-not $Sha256) {
    if (-not $CliZipPath) { throw 'Give -CliZipPath or -Sha256.' }
    $Sha256 = (Get-FileHash -LiteralPath $CliZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
if ($Sha256 -notmatch '^[0-9a-f]{64}$') {
    throw "'$Sha256' is not a lowercase SHA-256."
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$url = "https://github.com/KofTwentyTwo/gclo/releases/download/v$Version/gclo-cli-win-x64.zip"

function Set-Content-Preserving([string]$path, [string]$text) {
    # Keep the file's encoding (no BOM) and line endings as they are.
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($hasBom)))
}

# ---- Scoop: a real JSON edit, so the file stays valid whatever its layout.
$scoopPath = Join-Path $repoRoot 'bucket\gclo.json'
$scoop = Get-Content -LiteralPath $scoopPath -Raw | ConvertFrom-Json
$scoop.version = $Version
$scoop.architecture.'64bit'.url = $url
$scoop.architecture.'64bit'.hash = $Sha256
$scoopJson = ($scoop | ConvertTo-Json -Depth 10) -replace '\\u0026', '&'
# ConvertTo-Json indents with four spaces and escapes '$'-free text; Scoop reads either way.
Set-Content-Preserving $scoopPath ($scoopJson + "`n")
Write-Host "Scoop manifest -> $Version"

# ---- Chocolatey: nuspec version + release notes link, install script URL + checksum.
$nuspecPath = Join-Path $repoRoot 'packaging\chocolatey\gclo.nuspec'
$nuspec = Get-Content -LiteralPath $nuspecPath -Raw
$nuspec = $nuspec -replace '<version>[^<]+</version>', "<version>$Version</version>"
$nuspec = $nuspec -replace '<releaseNotes>[^<]+</releaseNotes>', "<releaseNotes>https://github.com/KofTwentyTwo/gclo/releases/tag/v$Version</releaseNotes>"
Set-Content-Preserving $nuspecPath $nuspec

$installPath = Join-Path $repoRoot 'packaging\chocolatey\tools\chocolateyinstall.ps1'
$install = Get-Content -LiteralPath $installPath -Raw
$install = $install -replace "url64bit\s*=\s*'[^']+'", "url64bit       = '$url'"
$install = $install -replace "checksum64\s*=\s*'[^']+'", "checksum64     = '$Sha256'"
Set-Content-Preserving $installPath $install
Write-Host "Chocolatey package -> $Version"

if ($env:GITHUB_OUTPUT) {
    Add-Content -Path $env:GITHUB_OUTPUT -Value "sha256=$Sha256"
}
