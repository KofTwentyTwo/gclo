$ErrorActionPreference = 'Stop'

# Installs the self-contained gclo CLI zip into the package's tools folder;
# Chocolatey shims gclo.exe onto PATH automatically. The URL and checksum are
# updated per stable release by .github/scripts/Update-PackageManifests.ps1.
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$packageArgs = @{
    packageName    = 'gclo'
    unzipLocation  = $toolsDir
    url64bit       = 'https://github.com/KofTwentyTwo/gclo/releases/download/v1.0.0/gclo-cli-win-x64.zip'
    checksum64     = '42805ca95a4f4d306df04f47a2b9aba54eee3df11cda8e3f201fe5a58311feee'
    checksumType64 = 'sha256'
}

Install-ChocolateyZipPackage @packageArgs
