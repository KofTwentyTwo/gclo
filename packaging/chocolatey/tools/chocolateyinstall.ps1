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
$packageArgs.url64bit = 'https://github.com/KofTwentyTwo/gclo/releases/download/v1.0.1/gclo-cli-win-x64-1.0.1.zip'
$packageArgs.checksum64 = '641ae4100022fba3fe489f8abe6ccc43186c070087d3edbf3a7891d5dc7428f7'
$packageArgs.checksumType64 = 'sha256'

Install-ChocolateyZipPackage @packageArgs
