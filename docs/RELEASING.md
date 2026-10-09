# Releasing gclo

Releases are fully automated by [`.github/workflows/release.yml`](../.github/workflows/release.yml).
Pushing a tag that starts with `v` builds, tests, packages, and publishes
everything. This document explains how versioning works, what gets published
where, and how to cut a release start to finish.

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

### How self-update channels map

The desktop app updates itself with [Velopack](https://velopack.io). Each
release is packed with `vpk pack --channel <stable|dev>`, which stamps the
channel into the installed app. An installed app only ever sees updates from
its own channel:

- Users who installed from a **stable** release (`gclo-stable-Setup.exe`) get
  updates only when you tag a new stable release.
- Users who installed from a **dev** release (`gclo-dev-Setup.exe`) get every
  prerelease you tag.

Velopack asset names include the channel. A stable release `v1.2.3` carries:

- `gclo-stable-Setup.exe` — the installer
- `gclo-stable-Portable.zip` — portable build
- `gclo-1.2.3-stable-full.nupkg` — full update package
- `gclo-1.2.3-stable-delta.nupkg` — delta from the previous stable release
  (absent on the first release of a channel)
- `gclo-cli-win-x64.zip` — self-contained single-file CLI

The workflow runs `vpk download github` before packing so deltas can be
generated against the previous release on the same channel; on the very first
release of a channel that step finds nothing and is allowed to fail — you just
get a release without a delta package, which is normal.

## What gets published, and which secrets enable it

| Target | Condition | Secret |
| ------ | --------- | ------ |
| GitHub Release (Setup, portable, full/delta packages, CLI zip) | always | none — the built-in `GITHUB_TOKEN` is enough |
| nuget.org (`gclo.Engine` package) | `NUGET_API_KEY` secret is set | `NUGET_API_KEY` — an API key from nuget.org with push rights for `gclo.Engine` |
| winget (`KofTwentyTwo.gclo`) | stable releases only, and `WINGET_TOKEN` secret is set | `WINGET_TOKEN` — a GitHub personal access token (classic, `public_repo` scope) used by `wingetcreate` to fork/PR [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) |
| `SHA256SUMS` and CycloneDX SBOMs on the GitHub Release | always | none |
| Scoop (`bucket/gclo.json` in this repository) | stable releases only | none — the workflow opens a PR against `main` with the updated manifest; merge it and `scoop update gclo` sees the release |
| Chocolatey (`gclo` on chocolatey.org) | stable releases only, and `CHOCO_API_KEY` secret is set | `CHOCO_API_KEY` — an API key from your chocolatey.org account; the first push creates the package (it then goes through Chocolatey moderation) |

If a secret is not configured, the corresponding step is skipped cleanly — the
release still succeeds. Configure secrets under
**Settings → Secrets and variables → Actions**.

## First winget submission (one-time, manual)

`wingetcreate update` can only update a package that already exists in
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs). The first
submission must be done by hand, once, after your first stable GitHub release
exists:

```powershell
# From any Windows machine:
Invoke-WebRequest https://aka.ms/wingetcreate/latest -OutFile wingetcreate.exe

# Interactive: prompts for package id (use KofTwentyTwo.gclo), publisher,
# license, description, etc., then opens a PR against microsoft/winget-pkgs.
.\wingetcreate.exe new https://github.com/KofTwentyTwo/gclo/releases/download/v1.0.0/gclo-stable-Setup.exe --token <your GitHub PAT>
```

Once that first PR is merged and the package is live, set the `WINGET_TOKEN`
secret and every subsequent stable release updates the manifest automatically
via `wingetcreate update KofTwentyTwo.gclo ... --submit`.

## Scoop and Chocolatey

Both install the same asset, `gclo-cli-win-x64.zip`, by URL and SHA-256.

- **Scoop** needs no account: this repository is its own bucket. Users run
  `scoop bucket add gclo https://github.com/KofTwentyTwo/gclo` once, then
  `scoop install gclo`. The manifest (`bucket/gclo.json`) carries `checkver` and
  `autoupdate` entries keyed on `SHA256SUMS`, so `scoop update` works as
  soon as the manifest on `main` names the new version. The release workflow
  therefore opens its "Packaging" pull request against `main` (three generated
  files, nothing else). Note that a PR opened with the default `GITHUB_TOKEN`
  triggers no status checks, so `DEPS_PAT` should be configured (#7) for the
  required checks to run on it.
- **Chocolatey** needs a one-time account on chocolatey.org and its API key as
  the `CHOCO_API_KEY` secret. The workflow packs `packaging/chocolatey` on every
  stable release and pushes it when the key exists; the first version goes
  through Chocolatey's moderation queue (expect a few days), later versions are
  usually auto-approved. Users then run `choco install gclo`.
- To re-point the manifests by hand (for example after a missed release):
  `./.github/scripts/Update-PackageManifests.ps1 -Version 1.2.3 -CliZipPath <zip>`.

## Cutting a release, start to finish

1. **Open the release checklist issue** from the KofTwentyTwo standards'
   [release checklist template](https://github.com/KofTwentyTwo/standards/blob/main/templates/release-checklist.md),
   titled `Release vX.Y.Z`, and work through it: the conformance checker
   (`tools/Test-RepoConformance.ps1 -Repository KofTwentyTwo/gclo`, attach the
   output), open scanning alerts, and for a MINOR or MAJOR the threat-model
   review (`docs/security/threat-model.md`). The release notes link the issue.

2. **Make sure `main` is green.** CI, the security scans, and CodeQL must all
   pass. The release workflow re-runs the CI gates against the tag, so a red
   `main` will fail the release anyway, just later.

3. **Pick the version.** Follow semver: breaking change → major, new feature →
   minor, fix → patch. Add a prerelease suffix (`-beta.1`) if this should go to
   the `dev` channel only.

4. **Tag and push** (the `protect-release-tags` ruleset lets only the
   repository admin create a `v*` tag):

   ```powershell
   git checkout main
   git pull
   git tag v1.2.3          # or v1.2.3-beta.1 for a dev-channel prerelease
   git push origin v1.2.3
   ```

5. **Watch the workflow.** The `Release` workflow appears under the Actions
   tab. It runs three jobs, each gated on the previous one:
   - **Verify tag** — validate the tag, derive version/channel, and check
     that the tagged commit is contained in `main`. A tag on any other commit
     fails here and nothing is built or published.
   - **Gates** — the full `CI` workflow, exactly as a pull request runs it:
     x64 build with warnings as errors, 100% line coverage on
     `gclo.Engine` / `gclo.ViewModels` / `gclo`, and the formatting gate.
     The FlaUI UI smoke suite runs as well but is **advisory** for a release,
     matching its status on `main` (not a required check yet, because UI
     automation on hosted runners is still proving itself). A red UI job
     shows in the run but does not stop publishing; look at it afterwards.
     If a *blocking* gate fails, fix the cause on `main` and cut a new patch
     version — never reuse the tag (see "If a release goes wrong").
   - **Build, package, and publish**, which will, in order:
     - publish and zip the CLI (with the LICENSE inside),
     - publish the WinUI app unpackaged and pack it with Velopack,
     - generate a CycloneDX SBOM per artifact, write `SHA256SUMS`, and attest
       provenance and SBOMs,
     - create a **draft** GitHub Release (marked prerelease for `dev`), upload
       all assets and the notes generated from the Conventional Commit titles
       since the previous release (`.github/scripts/New-ReleaseNotes.ps1`), then
       **publish** it — this repository uses immutable releases, so everything
       must land while the release is still a draft,
     - push `gclo.Engine` to nuget.org (if `NUGET_API_KEY` is set),
     - submit the winget manifest update (stable only, if `WINGET_TOKEN` is set).

6. **Verify.** Check the new release on the
   [releases page](https://github.com/KofTwentyTwo/gclo/releases): the Setup
   exe, portable zip, full package, CLI zip (and a delta package after the
   first release), `SHA256SUMS`, and the two SBOMs should all be attached;
   `gh attestation verify` passes for each asset. Tick the "Ship" section of
   the checklist issue. If the winget step ran, check your PR on
   microsoft/winget-pkgs.

7. **If something failed**, where it failed decides the recovery. The workflow
   is a single job, so "re-run failed jobs" always re-runs everything from the
   start — and because releases in this repository are **immutable**, a
   published release's tag and assets are frozen and cannot be re-uploaded.

   - **Failed before "Publish the completed release"** (build, tests,
     packaging, asset upload, changelog): nothing is public yet. Delete the
     draft release if one was created — drafts can always be deleted:

     ```powershell
     gh release delete v1.2.3 --yes
     ```

     Then fix the cause and re-run the job from the Actions UI; the tag is
     unchanged and everything is rebuilt from it.

   - **Failed after the release was published** (the NuGet push or the winget
     submission): the release itself is fine but frozen — do **not** re-run
     the job, which would try to recreate it. Run the missing step manually
     instead:

     ```powershell
     # NuGet push (needs an API key with push rights for gclo.Engine)
     dotnet pack gclo.Engine -c Release -p:Version=1.2.3 -o artifacts/nuget
     dotnet nuget push "artifacts/nuget/*.nupkg" --api-key <NUGET_API_KEY> --source https://api.nuget.org/v3/index.json --skip-duplicate

     # winget manifest update (stable releases only)
     Invoke-WebRequest https://aka.ms/wingetcreate/latest -OutFile wingetcreate.exe
     .\wingetcreate.exe update KofTwentyTwo.gclo --urls https://github.com/KofTwentyTwo/gclo/releases/download/v1.2.3/gclo-stable-Setup.exe --version 1.2.3 --submit --token <WINGET_TOKEN>
     ```

     If the published release itself is broken, do not try to fix it in
     place and do not delete it: see "A broken release" below.

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
