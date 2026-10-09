# ADR-0006: Retire the `dev` integration branch; `main` is the only long-lived branch

- **Status:** Accepted
- **Date:** 2026-10-09
- **Pull request:** #64

## Context

Until 1.0.1 the repository used a `dev` integration branch: feature work merged to
`dev` without enforced checks, and `dev` reached `main` through pull requests gated
by the required checks. The KofTwentyTwo standards require GitHub Flow with `main`
as the only long-lived branch, short-lived `<type>/<description>` topic branches,
squash merges, and every change gated on the same checks (K22-SDLC-10, -12, -16).
With one maintainer the two-branch model doubled the merges and let unreviewed code
sit on `dev`; the Scoop manifest, Dependabot, and the NuGet update workflow each
needed special handling to target the right branch.

## Options considered

### Option A: Keep `dev` and record an exception

- Pros: no process change.
- Cons: a MUST exception with no compensating control that `main`-only does not
  already provide; generated pull requests keep needing a second merge.

### Option B: GitHub Flow, `main` only

- Pros: one gated path for every change; release tags, Scoop, Dependabot, and the
  NuGet workflow all target `main`; history on `main` is one reviewed squash commit per
  change.
- Cons: nothing lands without passing the full gate, so experiments live on topic
  branches or forks.

## Decision

Option B. Topic branches off `main`, Conventional Commit titles, DCO sign-off, signed
commits, squash merge. The `dev` branch and its `protect-dev` ruleset are deleted by
the maintainer once the standards work is on `main` (#66). A
`release/<major>.<minor>` branch is created only to ship fixes to a still-supported
older line.

## Consequences

- CONTRIBUTING, the pull request template, Dependabot, the NuGet update workflow, and
  the release workflow all reference `main` only.
- Security: every commit on `main` is attributable (signed, signed off) and gated.
- Follow-up: delete `dev` and `protect-dev` (#66).
