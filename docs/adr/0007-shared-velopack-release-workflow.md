# ADR 0007: Release through the standards' shared Velopack workflow

- **Status:** Accepted
- **Date:** 2026-10-09
- **Issue:** [#76](https://github.com/KofTwentyTwo/gclo/issues/76)

## Context

Until 1.0.1 the release pipeline built, packed, attested and published inside this
repository's `release.yml`. The KofTwentyTwo standards require the build to run in a
shared reusable workflow (K22-CI-30) so the provenance attestation names a workflow
the calling repository cannot edit (SLSA Build L3); gclo carried exception EX-0004
until that workflow existed. It now does: `release-velopack.yml` in
KofTwentyTwo/standards builds, tests, gates coverage, publishes, packs with Velopack,
generates SBOMs with Syft, writes `SHA256SUMS`, attests, and publishes the release.

Publishing to Scoop, Chocolatey, winget and (optionally) NuGet is specific to gclo,
and the previous workflow skipped any of them whose secret was missing, so a green
run did not mean every channel had been published.

## Decision

- `release.yml` keeps only what is gclo's: the "tag is on main" check, the `ci`
  gates (K22-CI-31), the call to the shared workflow pinned to a commit, and the
  caller-side publishing jobs.
- Publishing jobs run in the `release` environment, which only `v*` tags can use,
  and **fail** when a token or variable they need is absent. NuGet publishing is an
  explicit opt-in (`NUGET_PUBLISH` variable) because gclo.Engine has no consumers
  yet; once opted in, a missing key fails too.
- Asset names follow the shared workflow: the CLI zip is
  `gclo-cli-win-x64-<version>.zip`, SBOMs are `gclo-<version>.cdx.json` and
  `gclo-cli-<version>.cdx.json`, and release notes are GitHub's generated notes.
  The Scoop and Chocolatey manifests, their updater script, and the tests follow.
- The LICENSE ships inside the published app and CLI through the project files,
  since the shared workflow does not add it.

## Consequences

- The provenance's signer is `KofTwentyTwo/standards`; verification commands pass
  `--signer-repo KofTwentyTwo/standards` (README, "Verifying a release").
- Exception EX-0004 closes after the first successful release on the shared
  workflow; the first winget submission is still manual (`wingetcreate new`).
- Jobs are separate, so a failed publisher can be re-run alone after its token is
  added, without rebuilding or touching the immutable release.
- Changes to the build itself happen in the standards repository and reach gclo by
  moving the pin.
