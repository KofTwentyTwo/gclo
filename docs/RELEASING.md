# Releasing gclo

Releases are automated by [`.github/workflows/release.yml`](../.github/workflows/release.yml),
which calls the KofTwentyTwo standards' shared
[`release-velopack.yml`](https://github.com/KofTwentyTwo/standards/blob/main/.github/workflows/release-velopack.yml)
for the build (ADR [0007](adr/0007-shared-velopack-release-workflow.md)). Pushing a
tag that starts with `v` builds, tests, packages, and publishes everything. This
document explains how versioning works, what gets published where, what must be
configured for a release to succeed, and how to cut one start to finish.

## Versioning and channels

The version is the tag with the leading `v` stripped. Tags must be strict
[semver](https://semver.org) (`vMAJOR.MINOR.PATCH` plus an optional prerelease
suffix). Build metadata (`+...`) is rejected.

| Tag            | Version        | GitHub Release | Velopack channel |
| -------------- | -------------- | -------------- | ---------------- |
| `v1.2.3`       | `1.2.3`        | stable         | `stable`         |
| `v1.2.3-beta.1`| `1.2.3-beta.1` | prerelease     | `dev`            |

Any prerelease identifier works (`-alpha`, `-beta.2`, `-rc.1`, ...) — the rule
is simply: **a `-` in the version means prerelease, which means the `dev`
channel**.

### The dev edition is a separate app

A prerelease is packed as its own Velopack app, id **`gclo-dev`**, while stable
releases use **`gclo`**. The two install side by side: separate install folder
(`%LOCALAPPDATA%\gclo-dev` vs `%LOCALAPPDATA%\gclo`), Start menu entry, update
feed and data root (`%LOCALAPPDATA%\KofTwentyTwo\gclo-dev` vs `…\gclo`), and
the dev edition's window title reads "gclo (dev)". A machine can therefore keep
the stable app for real work and a dev build for trying the next release; each
only ever updates within its own channel. The dev edition's assets follow its
app id: `gclo-dev-dev-Setup.exe`, `gclo-dev-dev-Portable.zip`,
`gclo-dev-1.2.3-beta.1-dev-full.nupkg`, `releases.dev.json`. The CLI zip is the
same for both (`gclo-cli-win-x64-<version>.zip`). Dev installs made before this
split (app id `gclo` on the dev channel, up to 1.0.2-beta.3) cannot update into
`gclo-dev`: uninstall them, or install the stable app over them.

### How self-update channels map

The desktop app updates itself with [Velopack](https://velopack.io). Each
release is packed with `vpk pack --channel <stable|dev>`, which stamps the
channel into the installed app. An installed app only ever sees updates from
its own channel:

- Users who installed from a **stable** release (`gclo-stable-Setup.exe`) get
  updates only when you tag a new stable release.
- Users who installed from a **dev** release (`gclo-dev-Setup.exe`) get every
  prerelease you tag.

A stable release `v1.2.3` carries:

- `gclo-stable-Setup.exe` — the installer
- `gclo-stable-Portable.zip` — portable build
- `gclo-1.2.3-stable-full.nupkg` — full update package
- `gclo-1.2.3-stable-delta.nupkg` — delta from the previous stable release
  (absent on the first release of a channel)
- `releases.stable.json` and `assets.stable.json` — the Velopack feed, limited to
  this build
- `gclo-cli-win-x64-1.2.3.zip` — self-contained single-file CLI, with the LICENSE
- `gclo-1.2.3.cdx.json`, `gclo-cli-1.2.3.cdx.json` — CycloneDX SBOMs
- `SHA256SUMS` — one line per asset

The shared workflow downloads the previous release on the same channel before
packing so deltas can be generated; on the very first release of a channel that
finds nothing, and the release simply has no delta package.

## What gets published, and what it needs

Everything a publishing job needs lives in the **`release` environment**
(Settings → Environments → `release`, deployment rule: tags matching `v*`) or in
repository variables. **A job whose token is missing fails** — a green run means
every enabled channel was published. A failed publisher can be re-run on its own
after the token is added ("Re-run failed jobs"); the release itself is not rebuilt.
winget is the one channel switched on explicitly (`WINGET_PACKAGE_ID`), because
its first submission cannot be automated; see below.

| Job | Runs for | Needs | Publishes |
| --- | -------- | ----- | --------- |
| `release` (shared workflow) | every tag | the signing identity, as `release`-environment **variables** (no secret exists): `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_SUBSCRIPTION_ID`, `TRUSTED_SIGNING_ENDPOINT`, `TRUSTED_SIGNING_ACCOUNT`, `TRUSTED_SIGNING_PROFILE` (see "Code signing") | the GitHub Release: Setup, portable, full/delta packages, feed, CLI zip, SBOMs, `SHA256SUMS`, provenance and SBOM attestations; every exe/dll and Setup.exe Authenticode-signed |
| `verify-signatures` | every tag | `TRUSTED_SIGNING_SUBJECT` (`release`-environment variable: the publisher name on the certificate, as validated by Azure) | nothing; fails the run when a shipped executable is unsigned, untimestamped or signed by another publisher |
| `packages` | stable only | `RELEASE_APP_PRIVATE_KEY` (private key of the release GitHub App, see below) plus the repository variable `RELEASE_APP_CLIENT_ID`, `CHOCO_API_KEY` (chocolatey.org) | the Chocolatey package pushed to chocolatey.org, and a `build(packaging): …` pull request against `main` with `bucket/gclo.json` and `packaging/chocolatey` pointed at the release |
| `winget` | stable only, once the repository variable `WINGET_PACKAGE_ID` is set (`KofTwentyTwo.gclo`, after the one-time manual submission below) | `WINGET_TOKEN` (fine-grained token that can fork and open pull requests on public repositories, ≤ 90 days); missing → the job fails | a manifest update PR on [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) |
| `nuget` | only when the repository variable `NUGET_PUBLISH` is `true` | `NUGET_API_KEY` (nuget.org, push rights for `gclo.Engine`) | `gclo.Engine` on nuget.org |

Why a GitHub App: `main` requires signed commits, a DCO sign-off, and the full set
of status checks. A pull request opened with the default `GITHUB_TOKEN` triggers
no checks, and the action that opens the PR cannot sign commits with a personal
access token. With a short-lived token minted from the App, GitHub signs the
commit, its author is the App's `[bot]` user (exempt from the DCO check), and the
checks run. Create the App once (the same pattern the standards repository uses):
GitHub App installed **only on this repository**, permissions Contents and Pull
requests **write**, no ruleset bypass. Store its client id as the Actions variable
`RELEASE_APP_CLIENT_ID` and its private key (`.pem`) as `RELEASE_APP_PRIVATE_KEY`
in the `release` environment; never as a repository secret or a committed file.

## Code signing

Every release is Authenticode-signed with **Azure Artifact Signing** (formerly Trusted
Signing): the shared workflow signs every published `.exe`/`.dll` (app and CLI) before
packing, and `vpk pack` signs `Setup.exe` and the update stub with the same identity
(`--azureTrustedSignFile`). Always SHA-256 with an RFC 3161 timestamp from
`http://timestamp.acs.microsoft.com`. `SHA256SUMS`, the Velopack feed and the
attestations are produced after signing, so they describe the signed files. The
`verify-signatures` job then downloads what was published and runs
`signtool verify /pa /all` on Setup.exe and every gclo executable (ADR
[0008](adr/0008-azure-artifact-signing.md)).

There is **no signing secret**: the release job logs in to Azure over GitHub OIDC
(`azure/login`) as an Entra application whose federated credential trusts only this
repository's `release` environment, and that application holds only the
*Artifact Signing Certificate Profile Signer* role on the certificate profile.
The identity is configuration, not a secret, so it lives in `release`-environment
variables:

| Variable | Value |
| --- | --- |
| `AZURE_TENANT_ID` | the Entra tenant GUID (not the directory domain) |
| `AZURE_CLIENT_ID` | the application (client) id of `gclo-release-signing` |
| `AZURE_SUBSCRIPTION_ID` | the subscription that holds the signing account |
| `TRUSTED_SIGNING_ENDPOINT` | the account's regional endpoint, e.g. `https://eus.codesigning.azure.net` |
| `TRUSTED_SIGNING_ACCOUNT` | the signing account name (`kof22signing`) |
| `TRUSTED_SIGNING_PROFILE` | the certificate profile name (`releases`) |
| `TRUSTED_SIGNING_SUBJECT` | the validated publisher name on the certificate, checked by `verify-signatures` |

A missing variable fails the Azure login, and the run with it; nothing unsigned is
published. The Azure side (application, federated credential, role assignment) is
administered interactively by the owner; `.github/scripts/Set-ReleaseSigning.ps1`
records the exact steps and is idempotent.

### Signing locally

Only for a one-off (for example re-signing a hotfix build by hand); releases are
signed in CI. You need SignTool (Windows SDK), the .NET 8 runtime, the
[Microsoft.ArtifactSigning.Client](https://www.nuget.org/packages/Microsoft.ArtifactSigning.Client)
package (the `dlib`), and an identity holding the *Certificate Profile Signer* role:

```powershell
# One-time: the dlib, and a metadata file with the same values as the variables above
nuget install Microsoft.ArtifactSigning.Client -x -OutputDirectory $env:LOCALAPPDATA\ArtifactSigning
@{ Endpoint = 'https://eus.codesigning.azure.net'; CodeSigningAccountName = 'kof22signing'; CertificateProfileName = 'releases' } |
   ConvertTo-Json | Set-Content $env:LOCALAPPDATA\ArtifactSigning\metadata.json

# Sign in as an authorized identity (the dlib uses the Azure CLI / Azure PowerShell credential)
az login --tenant <tenant GUID>

# Sign and verify
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe | Where-Object FullName -match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1
& $signtool sign /v /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 `
   /dlib "$env:LOCALAPPDATA\ArtifactSigning\Microsoft.ArtifactSigning.Client\bin\x64\Azure.CodeSigning.Dlib.dll" `
   /dmdf "$env:LOCALAPPDATA\ArtifactSigning\metadata.json" .\gclo.exe
& $signtool verify /pa /all /v .\gclo.exe
```

## First winget submission (one-time, manual)

`wingetcreate update` can only update a package that already exists in
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs). The first
submission is done by hand, once, after the first stable GitHub release exists
(until `WINGET_PACKAGE_ID` is set the `winget` job does not run):

```powershell
# From any Windows machine:
Invoke-WebRequest https://aka.ms/wingetcreate/latest -OutFile wingetcreate.exe

# Interactive: prompts for package id (use KofTwentyTwo.gclo), publisher,
# license, description, etc., then opens a PR against microsoft/winget-pkgs.
.\wingetcreate.exe new https://github.com/KofTwentyTwo/gclo/releases/download/v1.2.3/gclo-stable-Setup.exe --token <your GitHub PAT>
```

Once that first PR is merged and the package is live, set the repository variable
`WINGET_PACKAGE_ID=KofTwentyTwo.gclo` and the `WINGET_TOKEN` secret. Re-running the
`winget` job of the bootstrapped release is not needed (it is already in winget);
every later stable release updates the manifest automatically.

## Scoop and Chocolatey

Both install the same asset, `gclo-cli-win-x64-<version>.zip`, by URL and SHA-256.

- **Scoop** needs no account: this repository is its own bucket. Users run
  `scoop bucket add gclo https://github.com/KofTwentyTwo/gclo` once, then
  `scoop install gclo`. The manifest (`bucket/gclo.json`) carries `checkver` and
  `autoupdate` entries keyed on `SHA256SUMS`, so `scoop update` works as
  soon as the manifest on `main` names the new version — which is why the
  `packages` job opens its pull request against `main` (three generated files,
  nothing else). Merge it.
- **Chocolatey** needs a one-time account on chocolatey.org and its API key as
  `CHOCO_API_KEY`. The first version goes through Chocolatey's moderation queue
  (expect a few days); later versions are usually auto-approved. Users then run
  `choco install gclo`.
- To re-point the manifests by hand (for example after a missed release):
  `./.github/scripts/Update-PackageManifests.ps1 -Version 1.2.3 -CliZipPath <zip>`.

## Cutting a release, start to finish

1. **Open the release checklist issue** from the KofTwentyTwo standards'
   [release checklist template](https://github.com/KofTwentyTwo/standards/blob/main/templates/release-checklist.md),
   titled `Release vX.Y.Z`, and work through it: the conformance checker
   (`tools/Test-RepoConformance.ps1 -Repository KofTwentyTwo/gclo`, attach the
   output), open scanning alerts, and for a MINOR or MAJOR the threat-model
   review (`docs/security/threat-model.md`).

2. **Make sure `main` is green** and the `release` environment holds every
   token the table above lists for the kind of release you are cutting.

3. **Rehearse on a prerelease.** Tag `vX.Y.Z-beta.N` first: it runs the whole
   pipeline except the stable-only publishers, on the `dev` channel. Install
   `gclo-dev-Setup.exe` on a clean machine, run a Quick Sync, and let an
   installed earlier prerelease self-update to it. Only then tag the stable
   version.

4. **Pick the version.** Follow semver: breaking change → major, new feature →
   minor, fix → patch.

5. **Tag and push** (the `protect-release-tags` ruleset lets only the
   repository admin create a `v*` tag):

   ```powershell
   git checkout main
   git pull
   git tag -s v1.2.3          # or v1.2.3-beta.1 for a dev-channel prerelease
   git push origin v1.2.3
   ```

6. **Watch the workflow** under the Actions tab:
   - **Verify tag** — the tagged commit must be contained in `main`. A tag on
     any other commit fails here and nothing is built or published.
   - **Gates** — the full `ci` workflow, exactly as a pull request runs it,
     including the FlaUI end-to-end suite. If a gate fails, fix the cause on
     `main` and cut a new patch version; never reuse the tag.
   - **Smoke test the published app** — publishes the app with the shared
     workflow's exact commands and runs the FlaUI suite against that published
     `gclo.exe`. The gates test what `dotnet build` produces; this tests what
     ships (1.0.1 passed every gate and crashed at startup, #87). Nothing is
     published until it passes.
   - **Release** (shared workflow): locked restore, Release build with warnings
     as errors, tests with the 100% coverage gate, publish, Velopack pack, SBOMs,
     `SHA256SUMS`, provenance, a **draft** GitHub Release with every asset and
     generated notes, then **publish** — releases are immutable, so everything
     lands while the release is still a draft.
   - **Verify signatures on the published assets** — `signtool verify /pa /all`
     on Setup.exe and every gclo executable inside the portable and CLI zips,
     checking the publisher name and the timestamp. A failure here means a
     published release is unsigned: handle it as "A broken release".
   - **Scoop and Chocolatey**, **winget**, **NuGet** — the publishers described
     above; each fails loudly when it cannot publish.

7. **Verify.** On the [releases page](https://github.com/KofTwentyTwo/gclo/releases)
   every asset listed above is attached and
   `gh attestation verify <asset> --repo KofTwentyTwo/gclo --signer-repo KofTwentyTwo/standards`
   passes. Merge the packaging PR. Check the winget PR on microsoft/winget-pkgs.
   Tick the "Ship" section of the checklist issue.

8. **If something failed**, where it failed decides the recovery:

   - **In `verify`, `gates`, or inside `release` before "Publish release"**:
     nothing is public. Delete the draft release if one was created
     (`gh release delete v1.2.3 --yes`), fix the cause through a pull request,
     and re-run the workflow from the Actions UI; the tag is unchanged and
     everything is rebuilt from it. A fix that changes the shipped code needs a
     new patch version instead.
   - **In a publisher after the release was published**: the release is fine and
     frozen. Add the missing token or variable and **re-run the failed job
     only**; do not re-run the whole workflow, which would try to recreate the
     release. The manual equivalents, should a job keep failing:

     ```powershell
     # Scoop + Chocolatey manifests, then push the package and open the PR by hand
     ./.github/scripts/Update-PackageManifests.ps1 -Version 1.2.3 -CliZipPath .\gclo-cli-win-x64-1.2.3.zip
     choco pack packaging/chocolatey/gclo.nuspec --outputdirectory artifacts
     choco push artifacts\gclo.1.2.3.nupkg --source https://push.chocolatey.org/ --api-key <CHOCO_API_KEY>

     # winget manifest update (stable releases only)
     .\wingetcreate.exe update KofTwentyTwo.gclo --urls https://github.com/KofTwentyTwo/gclo/releases/download/v1.2.3/gclo-stable-Setup.exe --version 1.2.3 --submit --token <WINGET_TOKEN>
     ```

   - **The published release itself is broken**: do not fix it in place and do
     not delete it; see "A broken release".

### A broken release

Releases are immutable and a published version is never deleted or reused
(K22-REL-07): installed apps, package managers, and attestations may already
refer to it. Instead:

1. Edit the broken release's notes to say what is wrong and which version fixes
   it (`gh release edit v1.2.3 --notes-file ...`), and mark it as a prerelease if
   stable users must stop receiving it (`gh release edit v1.2.3 --prerelease`).
2. Fix the cause on `main` through a normal pull request.
3. Tag the **next** patch version and let the pipeline publish it.

The `protect-release-tags` ruleset blocks tag deletion and updates for everyone;
that is deliberate and is not to be bypassed.
