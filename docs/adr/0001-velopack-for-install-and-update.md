# ADR-0001: Use Velopack for install and self-update from GitHub Releases

- **Status:** Accepted
- **Date:** 2026-07-05 (recorded 2026-10-09)
- **Pull request:** #64

## Context

gclo is a per-user Windows desktop app that must install without administrator
rights, update itself in place, and offer a `stable` and a `dev` (prerelease) channel.
The project has one maintainer and no code-signing identity yet, so the installer
and updater must work with GitHub Releases alone, keep installed apps on their
channel, and never crash the app when an update check fails.

## Options considered

### Option A: MSIX through the Microsoft Store or App Installer

- Pros: platform-native updates, signing handled by the Store.
- Cons: a Store account and identity validation up front; sideloaded MSIX needs a
  trusted certificate on every machine; packaged-only builds complicate FlaUI testing
  and the unpackaged CLI companion.

### Option B: Squirrel.Windows / Clowd.Squirrel

- Pros: per-user install, delta updates, GitHub Releases as the feed.
- Cons: unmaintained (Squirrel) or superseded (Clowd.Squirrel is Velopack's ancestor).

### Option C: Velopack

- Pros: per-user install with no admin, delta packages, GitHub Releases as the update
  source, explicit channels, portable zip beside the installer, maintained, .NET-first.
- Cons: one more build-time tool to pin (`vpk`); installer not signed until an
  Authenticode identity exists (standards exception EX-0002).

## Decision

Velopack (Option C). The release workflow runs `vpk pack` on an unpackaged
self-contained publish and `vpk upload github` creates a draft release; the app runs
`VelopackApp.Build().Run()` first in `Program.Main`. Tags with a prerelease suffix go
to the `dev` channel, plain tags to `stable`; an install never crosses channels.

## Consequences

- Updates come only from this repository's GitHub Releases over HTTPS, verified
  against the channel's `releases.<channel>.json` feed.
- The app must stay buildable unpackaged (`WindowsPackageType=None`), which the CI
  build and the UI tests enforce.
- Security: the update feed is a trust boundary (threat model T1); immutable releases,
  `SHA256SUMS`, and build-provenance attestations mitigate it until Authenticode
  signing closes EX-0002 (#57).
- Follow-up: self-update verification on a dev-channel install (#56).
