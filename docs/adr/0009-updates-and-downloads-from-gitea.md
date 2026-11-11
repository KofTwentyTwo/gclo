# 0009. Updates and release downloads come from the project's Gitea server

- Status: accepted
- Date: 2026-11-10
- Amends: [0001](0001-velopack-for-install-and-update.md) (the update feed), [0003](0003-repository-as-scoop-bucket.md) (where the bucket and its downloads are)

## Context

The project's home moved from GitHub to `git.kof22.com` (#165): source, issues, and
the releases with their assets. GitHub keeps a read-only copy. ADR 0001 chose
Velopack with GitHub Releases as the update feed, and the Scoop and Chocolatey
manifests download from GitHub Releases. Left as they were, the app would look for
updates in a place where new releases no longer appear, and the package managers
would install an old version for ever.

## Decision

- The in-app updater reads the releases of `https://git.kof22.com/KofTwentyTwo/gclo`
  through Velopack's `GiteaSource`, anonymously. The repository is public; no token
  is built into the app.
- The Scoop manifest (homepage, download URL, version check, auto-update) and the
  Chocolatey package (download URL, release notes link) name the same server. The
  script that regenerates them for a release writes those URLs.
- A release is complete only when its assets, including `releases.<channel>.json`
  and `SHA256SUMS`, are attached to the release on `git.kof22.com`. Where the
  release is built is a separate matter (see Consequences).

## Consequences

- Versions up to 1.1.0 look for updates on GitHub. They find a later version only
  if it is also published there; otherwise they are updated once by hand. From
  1.1.1 on the app looks on `git.kof22.com`.
- The availability and the integrity of updates now rest on a self-hosted server
  instead of GitHub's release hosting. The updater verifies a package against the
  hash in the feed, and the feed comes from the same server; it checks no signature
  and no attestation (it never did). Whoever can publish a release on that server,
  or holds its TLS certificate or DNS, can therefore deliver an update that installs
  without a warning. Authenticode signing (ADR 0008) protects a download a person
  runs, not an update the app applies. The threat model's T1 says the same and is
  marked accepted, not mitigated.
- The server must be reachable from wherever the app runs. An app outside the
  network that can reach `git.kof22.com` reports that it could not check for
  updates, and Scoop cannot add the bucket.
- Velopack reads the ten newest releases of any kind and then picks by channel.
  More than nine prereleases published after the latest stable release would hide
  that release from stable installs. Keep fewer, or delete old prereleases.
- Build-provenance attestations are a GitHub feature. While releases are still
  built by the GitHub workflow and copied over, they exist and are verified against
  GitHub as the README describes; a release built on Gitea needs another answer.
- The release workflow itself (`.github/workflows/release.yml`) publishes to GitHub
  today. Until a pipeline on Gitea exists, publishing a release includes copying its
  assets to the Gitea release. That step is part of the release procedure, not of
  this decision.
- Anonymous requests to the server are subject to its own rate limits; the updater
  asks once at startup and when the user asks.

## Alternatives considered

- **Keep the update feed on GitHub.** Rejected: the owner's decision is that
  everything a user touches comes from the project's own server, and a feed on a
  read-only copy would silently stop offering updates the day the copy stops being
  fed.
- **A plain web folder (`SimpleWebSource`).** Rejected: Gitea's release pages are
  already there, and a second place to publish to is a second place to forget.
