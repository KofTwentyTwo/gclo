# ADR-0003: Make this repository its own Scoop bucket and ship the Chocolatey package source

- **Status:** Accepted
- **Date:** 2026-10-09
- **Pull request:** #59

## Context

Users asked for package-manager installs of the CLI (#9). Scoop needs a bucket (a
git repository with `bucket/<app>.json`) read from the default branch; Chocolatey
needs a chocolatey.org account, an API key, and moderation of the first push. Both
install the same release asset, `gclo-cli-win-x64-<version>.zip`, by URL and SHA-256, so their
manifests must be updated on every stable release without hand edits.

## Options considered

### Option A: Submit to the community Scoop "extras" bucket and the Chocolatey community feed

- Pros: discoverable without `scoop bucket add`.
- Cons: every release waits on third-party review; a maintainer-less lag between
  release and availability; no control over manifest content.

### Option B: A separate `KofTwentyTwo/scoop-bucket` repository

- Pros: keeps manifests out of the product repository.
- Cons: a second repository to protect, release-gate, and keep in sync; cross-repo
  automation needs a token.

### Option C: This repository is the bucket; Chocolatey source lives in `packaging/`

- Pros: one repository, one ruleset, one release workflow; the release job rewrites
  both manifests from one source of truth and opens a pull request against `main`
  (where Scoop reads the manifest), so `scoop update` sees a release as soon as that
  pull request merges.
- Cons: a generated pull request per stable release; the Chocolatey push waits on the
  `CHOCO_API_KEY` secret (#7).

## Decision

Option C. `bucket/gclo.json` (`checkver: github`, `autoupdate` keyed on the release's
`SHA256SUMS`), `packaging/chocolatey`, and
`.github/scripts/Update-PackageManifests.ps1` as the single writer of version, URL,
and hash; `PackagingManifestTests` keeps both manifests valid and in agreement.

## Consequences

- Stable releases are consumable via `scoop bucket add gclo <repo>` with no external
  account; prereleases never touch package managers.
- The generated manifest pull request targets `main` and goes through the normal
  gates: it is opened with a short-lived release GitHub App token, so its commit
  is signed by GitHub, exempt from the DCO check as a bot, and runs the required
  status checks (#85).
- Security: a new distribution channel; both channels verify the asset hash
  (threat model T2), and the hash they pin is the one `SHA256SUMS` attests.
- Follow-up: `CHOCO_API_KEY` and the first Chocolatey moderation (#7).
