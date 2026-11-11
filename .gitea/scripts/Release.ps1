#requires -Version 7
# gclo release on a Gitea Windows host runner. Mirrors the shared Velopack workflow's commands
# (standards release-velopack.yml): locked restore, Release build, tests and 100% coverage,
# self-contained publish, sign owned binaries, vpk pack (signing Velopack helpers), verify, publish.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest

$tag = $env:GITHUB_REF_NAME
if ($env:GITHUB_REF_TYPE -ne 'tag') { throw "Releases run only for tags; got '$tag'." }
$semver = '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*)?$'
if ($tag -cnotmatch $semver) { throw "Tag '$tag' is not vMAJOR.MINOR.PATCH with an optional prerelease suffix." }
$version = $tag.Substring(1)
$prerelease = $version.Contains('-')
$channel = if ($prerelease) { 'dev' } else { 'stable' }
$appId = if ($prerelease) { 'gclo-dev' } else { 'gclo' }

# Fail before building: every signing input must be present.
foreach ($name in 'AZURE_CLIENT_ID', 'AZURE_TENANT_ID', 'AZURE_CLIENT_SECRET', 'ARTIFACT_SIGNING_ENDPOINT', 'ARTIFACT_SIGNING_ACCOUNT', 'ARTIFACT_SIGNING_PROFILE', 'ARTIFACT_SIGNING_SUBJECT') {
   if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) { throw "Missing signing configuration: $name" }
}

# Releases are cut from main.
git fetch --quiet origin main
if ((git rev-parse --is-shallow-repository) -eq 'true') { git fetch --quiet --unshallow origin }
git merge-base --is-ancestor $env:GITHUB_SHA origin/main
Write-Output "OK: $tag is contained in main."

dotnet restore gclo.slnx -p:Platform=x64 --locked-mode
dotnet build gclo.slnx -c Release -p:Platform=x64 "-p:Version=$version" -warnaserror --no-restore
foreach ($project in 'gclo.Engine.Tests', 'gclo.Cli.Tests') {
   dotnet test $project -c Release --no-build --settings coverage.runsettings --collect:'XPlat Code Coverage' --results-directory TestResults
}
$reports = Get-ChildItem TestResults -Recurse -Filter coverage.cobertura.xml | ForEach-Object { [xml](Get-Content -LiteralPath $_.FullName -Raw) }
if (-not $reports) { throw 'No coverage reports.' }
foreach ($name in 'gclo.Engine', 'gclo.ViewModels', 'gclo') {
   $rates = foreach ($r in $reports) { $p = @($r.coverage.packages.package) | Where-Object { $_.name -eq $name }; if ($p) { [double]::Parse($p.'line-rate', [Globalization.CultureInfo]::InvariantCulture) } }
   if (-not $rates -or ($rates | Measure-Object -Maximum).Maximum -lt 1.0) { throw "Coverage gate failed for $name." }
}

dotnet publish gclo/gclo.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained "-p:Version=$version" -p:RestoreLockedMode=true -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=false -o publish/app
dotnet publish gclo.Cli/gclo.Cli.csproj -c Release -r win-x64 --self-contained "-p:Version=$version" -p:RestoreLockedMode=true -p:PublishSingleFile=true -o publish/cli

# Signing tools: x64 SignTool plus the Artifact Signing dlib, authenticating as the Entra app
# (EnvironmentCredential only; every other credential type is excluded).
$tools = Join-Path $env:RUNNER_TEMP 'ArtifactSigning'
New-Item -ItemType Directory -Force -Path $tools | Out-Null
$nuget = Join-Path $tools 'nuget.exe'
Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/v6.12.1/nuget.exe' -OutFile $nuget
& $nuget install Microsoft.ArtifactSigning.Client -x -OutputDirectory $tools | Out-Null
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe | Where-Object FullName -Match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1
$dlib = Get-ChildItem $tools -Recurse -Filter Azure.CodeSigning.Dlib.dll | Where-Object FullName -Match '\\x64\\' | Select-Object -First 1
if (-not $signtool -or -not $dlib) { throw 'x64 SignTool or the Artifact Signing dlib was not found.' }
$metadata = Join-Path $tools 'metadata.json'
@{
   Endpoint = $env:ARTIFACT_SIGNING_ENDPOINT
   CodeSigningAccountName = $env:ARTIFACT_SIGNING_ACCOUNT
   CertificateProfileName = $env:ARTIFACT_SIGNING_PROFILE
   ExcludeCredentials = @('WorkloadIdentityCredential', 'ManagedIdentityCredential', 'SharedTokenCacheCredential', 'VisualStudioCredential', 'VisualStudioCodeCredential', 'AzurePowerShellCredential', 'AzureCliCredential', 'AzureDeveloperCliCredential', 'InteractiveBrowserCredential')
} | ConvertTo-Json | Set-Content -LiteralPath $metadata -Encoding utf8

function Test-Subject($file) {
   $sig = Get-AuthenticodeSignature -LiteralPath $file
   $sig.Status -eq 'Valid' -and $sig.SignerCertificate.Subject -like "*$env:ARTIFACT_SIGNING_SUBJECT*" -and $null -ne $sig.TimeStamperCertificate
}
function Sign-File($file) {
   $current = Get-AuthenticodeSignature -LiteralPath $file
   if ($current.Status -eq 'Valid') { return }
   if ($current.Status -ne 'NotSigned') { throw "Refusing to replace an invalid signature on $file." }
   & $signtool.FullName sign /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /dlib $dlib.FullName /dmdf $metadata $file
   & $signtool.FullName verify /pa /all /v $file
}

# Only this project's own binaries; third-party and framework files keep their publishers' signatures.
$owned = Get-ChildItem publish -Recurse -File | Where-Object { $_.Name -match '^gclo.*\.(exe|dll)$' }
if (-not $owned) { throw 'No owned binaries to sign.' }
$owned | ForEach-Object { Sign-File $_.FullName }

# Velopack: pack, signing only its own helpers (Squirrel/stubs) and Setup.exe with the same identity.
dotnet tool install --global vpk --version 1.2.161
$signOne = Join-Path $tools 'sign-one.ps1'
$template = @'
param([Parameter(Mandatory)][string] $File)
$sig = Get-AuthenticodeSignature -LiteralPath $File
if ($sig.Status -eq 'Valid') { exit 0 }
if ($sig.Status -ne 'NotSigned') { throw "Refusing to replace an invalid signature on $File." }
& '@@TOOL@@' sign /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /dlib '@@DLIB@@' /dmdf '@@META@@' $File
if ($LASTEXITCODE -ne 0) { throw "Signing failed on $File." }
'@
$template.Replace('@@TOOL@@', $signtool.FullName).Replace('@@DLIB@@', $dlib.FullName).Replace('@@META@@', $metadata) |
   Set-Content -LiteralPath $signOne -Encoding utf8NoBOM
$exclude = '(?i)[\\/](?!(?:Squirrel\.exe|.*_ExecutionStub\.exe)$)[^\\/]+$'
$env:VELOPACK_TEMP = Join-Path $env:RUNNER_TEMP 'k22-velopack'
# No Gitea delta baseline exists for the first release; later releases can add one.
vpk pack --runtime win-x64 --packId $appId --packVersion $version --packDir publish/app --mainExe gclo.exe --channel $channel --icon gclo/Assets/Brand/gclo.ico --outputDir Releases --signTemplate "pwsh -NoProfile -NonInteractive -File `"$signOne`" `"{{file}}`"" --signExclude $exclude

New-Item -ItemType Directory -Force -Path release | Out-Null
$assets = @(Get-Content -LiteralPath "Releases/assets.$channel.json" -Raw | ConvertFrom-Json)
if ($assets.Count -eq 0) { throw "vpk produced no assets for channel $channel." }
$names = foreach ($a in $assets) { Copy-Item -LiteralPath (Join-Path Releases $a.RelativeFileName) -Destination release; [IO.Path]::GetFileName($a.RelativeFileName) }
$feed = Get-Content -LiteralPath "Releases/releases.$channel.json" -Raw | ConvertFrom-Json
$feed.Assets = @($feed.Assets | Where-Object { $_.FileName -in $names })
$feed | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath "release/releases.$channel.json" -Encoding utf8NoBOM
Compress-Archive -Path publish/cli/* -DestinationPath "release/$appId-cli-win-x64-$version.zip"

# Verify what ships: Setup.exe and the CLI exe must be validly signed, timestamped, by the expected subject.
$setup = Get-ChildItem release -Filter '*-Setup.exe' | Select-Object -First 1
if (-not $setup -or -not (Test-Subject $setup.FullName)) { throw 'Setup.exe is not signed by the expected publisher.' }
foreach ($f in Get-ChildItem publish/cli -Filter 'gclo*.exe') { if (-not (Test-Subject $f.FullName)) { throw "$($f.Name) is not signed by the expected publisher." } }

# Checksums and Gitea release (draft until every asset is uploaded).
$files = Get-ChildItem release -File
$sums = Join-Path $env:RUNNER_TEMP 'SHA256SUMS'
$files | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } | Set-Content -LiteralPath $sums -Encoding ascii
$api = "$env:GITHUB_SERVER_URL/api/v1/repos/$env:GITHUB_REPOSITORY"
$headers = @{ Authorization = "token $env:TOKEN" }
$body = @{ tag_name = $tag; name = $tag; prerelease = $prerelease; draft = $true } | ConvertTo-Json
$release = Invoke-RestMethod -Method Post -Headers $headers -ContentType 'application/json' -Body $body "$api/releases"
foreach ($f in @($files.FullName) + $sums) {
   $leaf = [uri]::EscapeDataString([IO.Path]::GetFileName($f))
   $null = Invoke-RestMethod -Method Post -Headers $headers -Form @{ attachment = Get-Item -LiteralPath $f } "$api/releases/$($release.id)/assets?name=$leaf"
}
$null = Invoke-RestMethod -Method Patch -Headers $headers -ContentType 'application/json' -Body (@{ draft = $false } | ConvertTo-Json) "$api/releases/$($release.id)"
Write-Output "Published signed $tag to $($release.html_url)"
