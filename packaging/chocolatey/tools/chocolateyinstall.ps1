Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

################################################################################
## Installs the self-contained gclo CLI zip into the package's tools folder;  ##
## Chocolatey shims gclo.exe onto PATH automatically. The URL and checksum    ##
## are updated per stable release by Update-PackageManifests.ps1.             ##
################################################################################
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{}
$packageArgs.packageName = 'gclo'
$packageArgs.unzipLocation = $toolsDir
$packageArgs.url64bit = 'https://github.com/KofTwentyTwo/gclo/releases/download/v1.1.0/gclo-cli-win-x64-1.1.0.zip'
$packageArgs.checksum64 = '46f02d89c9d8a459e5b02598f9791fa7aab5c16b09c254bc997f8f235a6a49e2'
$packageArgs.checksumType64 = 'sha256'

Install-ChocolateyZipPackage @packageArgs
