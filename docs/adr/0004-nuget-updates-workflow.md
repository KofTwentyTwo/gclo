# ADR-0004: Update NuGet packages with a scheduled workflow instead of Dependabot

- **Status:** Accepted
- **Date:** 2026-10-09 (decision taken 2026-10-09 in #52)
- **Pull request:** #51

## Context

The standards require Dependabot (or Renovate) for every ecosystem with a cooldown
(K22-DEP-20). Dependabot's NuGet updater cannot evaluate the WinUI project's
Windows-versioned target framework (`net10.0-windows10.0.19041.0`,
dependabot/dependabot-core#13923): it reports every newer package as "not
compatible" and, because it updates a package consistently across projects, blocks
the same packages in the plain `net10.0` projects too. In months of operation it
delivered no NuGet update at all.

## Options considered

### Option A: Keep Dependabot for NuGet and wait for the upstream fix

- Pros: standard tooling, no custom code.
- Cons: no updates arrive; security alerts still fire but the fix PRs cannot be made.

### Option B: Renovate

- Pros: evaluates frameworks through its own NuGet datasource; cooldown support.
- Cons: a GitHub App with write access to the repository, a large configuration
  surface, and a second update bot to review.

### Option C: A weekly workflow driven by `dotnet list package --outdated`

- Pros: evaluates through real MSBuild and NuGet, so the WinUI project is handled
  correctly; runs the same gates CI does against the bumped tree; opens one pull
  request with a report; honors the 7-day cooldown by asking nuget.org for publish
  dates; major bumps are reported but not applied unless requested.
- Cons: custom script to maintain (`.github/scripts/Update-NuGetPackages.ps1`); the
  pull request only gets its own status checks when `DEPS_PAT` is configured.

## Decision

Option C for NuGet; Dependabot stays for GitHub Actions (with the same cooldown).
Dependabot security alerts for NuGet continue to work because they come from the
dependency graph, not the updater.

## Consequences

- Weekly `build(deps): weekly NuGet package updates` pull requests against `main`
  with the gate results in the body.
- The script moves to central package management (`Directory.Packages.props`) with
  the Kingsrook style adoption (#65).
- Security: updates follow the K22-DEP-20 cooldown; security updates are triaged
  from Dependabot alerts immediately.
- Follow-up: revisit when dependabot-core#13923 is fixed; `DEPS_PAT` (#7).
