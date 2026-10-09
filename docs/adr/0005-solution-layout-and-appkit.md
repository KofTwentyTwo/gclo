# ADR-0005: Keep the flat solution layout with Engine and ViewModels libraries; adopt AppKit incrementally

- **Status:** Accepted
- **Date:** 2026-10-09
- **Pull request:** #64

## Context

The KofTwentyTwo
[Windows desktop reference architecture](https://github.com/KofTwentyTwo/standards/blob/main/architecture/windows-desktop-app.md)
describes `src/<App>`, `src/<App>.Core`, `src/<App>.Cli`, `tests/...`, with shared
plumbing (identity, paths, logging, settings, secrets, splash, About, updates) coming
from the `KofTwentyTwo.AppKit*` packages. gclo predates that architecture and is the
design it was distilled from: it has a flat layout with `gclo` (shell), `gclo.Engine`
(published NuGet library: GitHub, git, path validation), `gclo.ViewModels`
(UI-free presentation and settings), `gclo.Cli`, and three test projects, with its
own implementations of the plumbing AppKit now packages.

## Options considered

### Option A: Restructure now into `src/` and `tests/` with a single `gclo.Core`

- Pros: matches the reference layout one to one; the standard `.editorconfig` test
  relaxations key on `tests/`.
- Cons: merges a published library (`gclo.Engine`, consumed as a NuGet package) with
  view models that reference CommunityToolkit.Mvvm, changing the package's surface and
  dependencies for consumers; a large rename with no user value in a PATCH release.

### Option B: Replace the in-repo plumbing with AppKit now

- Pros: less code to maintain here.
- Cons: AppKit is itself "adoption in progress"; swapping the splash, settings store,
  vault, log window, and update coordinator at once is a MINOR-scale change with UI
  regressions to re-test.

### Option C: Keep the layout; record the deviation; adopt AppKit package by package

- Pros: no churn in the published library; each AppKit adoption is its own reviewed
  change with its own tests.
- Cons: two library projects where the reference has one; test-relaxation globs are
  spelled per project in `.editorconfig`.

## Decision

Option C. `gclo.Engine` + `gclo.ViewModels` together play the role of `<App>.Core`
(both under the 100% coverage gate); test projects stay beside them. AppKit packages
are adopted one at a time when they cover a subsystem gclo has (first candidates:
paths and settings store, then the update coordinator), each in its own pull request.

## Consequences

- The reference architecture's layout rule is met in substance (UI project holds
  views and plumbing only; logic in covered libraries) but not in folder names; this
  ADR is the record the architecture asks for.
- `.editorconfig` applies the test-project relaxations by project path rather than a
  `tests/` glob.
- Security: none.
- Follow-up: AppKit adoption issues as each package becomes a fit.
