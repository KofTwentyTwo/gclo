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

## Cutting a release, start to finish

1. **Make sure `main` is green.** The CI, CodeQL, and (for PRs) dependency
   review workflows must all pass. The release workflow re-runs the build with
   `-warnaserror` and the test suite, so a red `main` will fail the release
   anyway — just later and more annoyingly.

2. **Pick the version.** Follow semver: breaking change → major, new feature →
   minor, fix → patch. Add a prerelease suffix (`-beta.1`) if this should go to
   the `dev` channel only.

3. **Tag and push:**

   ```powershell
   git checkout main
   git pull
   git tag v1.2.3          # or v1.2.3-beta.1 for a dev-channel prerelease
   git push origin v1.2.3
   ```

4. **Watch the workflow.** The `Release` workflow appears under the Actions
   tab. It is a single job that will, in order:
   - validate the tag and derive version/channel,
   - build the solution (x64, warnings as errors) and run the tests,
   - publish and zip the CLI,
   - publish the WinUI app unpackaged and pack it with Velopack,
   - create a **draft** GitHub Release (marked prerelease for `dev`), upload
     all assets and the generated changelog to it, then **publish** it — this
     repository uses immutable releases, so everything must land while the
     release is still a draft,
   - push `gclo.Engine` to nuget.org (if `NUGET_API_KEY` is set),
   - submit the winget manifest update (stable only, if `WINGET_TOKEN` is set).

5. **Verify.** Check the new release on the
   [releases page](https://github.com/KofTwentyTwo/gclo/releases): the Setup
   exe, portable zip, full package, CLI zip (and a delta package after the
   first release) should all be attached. If the winget step ran, check your
   PR on microsoft/winget-pkgs.

6. **If something failed**, where it failed decides the recovery. The workflow
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
     place — delete it and its tag (below) and cut a new patch version.

### Deleting a bad release

If a release is broken, delete the GitHub Release *and* the tag, fix the
problem, and tag again with a **new** patch version. Never reuse a version
number that was published — installed apps and package managers may have
already seen it. With immutable releases this is the only fix for a bad
published release: its assets cannot be swapped in place.

```powershell
gh release delete v1.2.3 --yes
git push origin :refs/tags/v1.2.3
```
