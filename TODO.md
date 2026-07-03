# gclo — working TODO

The operating plan for gclo. Every item traces to an owner request. Statuses:
`[x]` done · `[~]` in progress · `[ ]` queued. Ordering within "Queued" is the
planned execution order.

## Done

- [x] **T1. WinUI 3 desktop app (.NET 10, packaged)** — clones AND updates every repo
  in a GitHub org; bounded parallelism (default 8); live per-repo status
  (queued/cloning/pulling/done/failed + error text); overall progress bar; failures
  on one repo never abort the others; cancellation.
- [x] **T2. Business logic in a shared library (`gclo.Engine`)** — Octokit listing
  (manual pagination, cancellable, deduped), LibGit2Sharp clone / fetch +
  fast-forward-only pull, orchestrator. No UI dependencies. (See T20 for the
  remaining view-model extraction.)
- [x] **T3. Headless unit tests** — 20 xunit tests: clone-vs-pull routing, concurrency
  bound, failure isolation, cancellation, progress ordering, validation.
- [x] **T4. Clean build + verified launch** — `dotnet build gclo.slnx -p:Platform=x64`
  with 0 errors / 0 warnings; app launch verified.
- [x] **T5. Multi-agent development flow** — research, development, adversarial review
  (13 confirmed findings fixed), and feature workflows run by agent sets.
- [x] **T6. Published to GitHub** — public repo <https://github.com/KofTwentyTwo/gclo>,
  MIT, description + topics set.
- [x] **T7. Token-first flow with org dropdown** — PAT entered first; editable
  dropdown auto-populates (debounced, cancellable) with what the token can access;
  manual entry still works for tokens that cannot enumerate orgs.
- [x] **T8. Personal accounts as sync targets** — the token's own login is listed
  first in the dropdown; repo listing falls back for user accounts (own account
  includes private repos via owner affiliation; other accounts list public repos).
- [x] **T9. Menu bar** — File (Settings…, Exit), Help (View on GitHub,
  Check for updates…, About + F1).
- [x] **T10. Settings dialog** — default target folder, default parallelism,
  Light/Dark/System theme; persisted to `%LOCALAPPDATA%\gclo\settings.json`
  (works packaged and unpackaged); applied at startup and on save.
- [x] **T11. About dialog** — version, MIT notice, full open-source attributions
  (Octokit, LibGit2Sharp, libgit2 GPLv2 + linking exception, MVVM Toolkit,
  Windows App SDK, .NET).
- [x] **T12. Open-source marketing/branding** — MIT LICENSE, README with badges and
  attribution table, CONTRIBUTING.md, generated sync-glyph icon at all asset sizes,
  manifest description, repo description + topics.
- [x] **T13. Repo security & branch model** — `dev` (integration) + `main` (stable,
  PR-only) with rulesets (no force-push/deletion); Dependabot alerts + automated
  security-update PRs; secret scanning with push protection; private vulnerability
  reporting; Actions tokens read-only by default.

## In progress (productionize agent workflow)

- [x] **T14. Full automated test coverage** — 35 tests: 15 new real-git integration
  tests for the LibGit2Sharp layer against local fixture repos (clone, FF pull,
  unborn-HEAD repair, diverged, detached, cleanup, cancellation); engine line
  coverage 60.6% (LibGit2GitClient 76%, orchestrator 100%; Octokit wrappers are
  network code). View-model coverage arrives with T20.
- [x] **T15. CLI / scriptable version (`gclo.Cli`)** — `gclo sync`, `gclo orgs`,
  `--json`, exit codes (0 ok / 1 partial / 2 fatal), token via env/file/stdin
  (never a bare argument), Ctrl+C cancellation, docs/CLI.md. Verified against live
  GitHub (see T21).
- [~] **T16. GitHub CI/CD** — workflows written (PR/push gates; tag-driven releases:
  `v1.2.3` = stable, `v1.2.3-beta.1` = dev prerelease; GitHub Releases + winget
  gated on WINGET_TOKEN + NuGet gated on NUGET_API_KEY; docs/RELEASING.md).
  In progress until the first real Actions runs are green.
- [~] **T17. Installer** — Velopack 1.2.0 wired (chosen over MSI/MSIX: MSIX needs a
  paid signing cert, MSI has no self-update; Velopack gives installer + delta
  updates from GitHub Releases). In progress until T25 produces a real Setup.exe.
- [x] **T18. Self-update from the GUI** — Help → "Check for updates…" via Velopack
  UpdateManager against the repo's releases; update-and-restart; degrades cleanly
  in dev builds (verified). Full loop proven at T25.
- [~] **T19. CI quality & security gates** — `-warnaserror` (also fails on vulnerable
  NuGet packages), tests + engine coverage threshold, `dotnet format` gate (verified
  locally), CodeQL, dependency review on PRs, Dependabot config. In progress until
  green on GitHub.
- [x] **T21. End-to-end smoke test with real credentials** — verified live: `orgs`
  lists personal account first + all 8 orgs; MMLT-Holdings cloned in parallel
  (exit 0); re-run took the pull path (updated=2, correct JSON).

## Queued (execution order)

- [ ] **T22. Windows-invalid path validation + recovery (spec)** — **PRIORITY: owner
  reports frequent real-world failures on Linux-fine/Windows-invalid paths and
  too-long paths.** Quick win first: two-phase clone (Checkout=false) enables
  setting `core.longpaths=true` before checkout, which should eliminate the
  path-length class outright; then the full validation/recovery below. Clone with
  Checkout=false; validate tree paths (trailing space/dot, invalid chars, reserved
  names, case-insensitive collisions, full-path length); structured result with
  per-path reasons + suggested sanitized names; recovery via rename/skip mapping
  with manual blob materialization; mapped repos fetch + re-materialize on update;
  abort keeps the existing didn't-exist-before cleanup; plumbing-built test fixtures.
- [ ] **T20. Extract `gclo.ViewModels` library** — move MainViewModel,
  RepoItemViewModel, AppSettings persistence out of the UI project; add view-model
  unit tests; UI project keeps only XAML/dialogs/pickers/brushes/update plumbing.
  Rule: no business logic in the UI layer, ever.
- [ ] **T23. UX overhaul (main screen)** — two-phase flow (Load repos → selectable
  table → Sync selected); checkbox column + select-all; sortable headers; per-row
  progress bar (determinate clone %, indeterminate pull); always-on activity log in
  the shared library (`%LOCALAPPDATA%\gclo\logs`) with View → Activity log viewer +
  open-logs-folder; clean error capture with Retry-failed (engine overload syncing
  an explicit repo list) and the T22 recovery dialog; Open-folder button after runs;
  live target-path preview + optional "create org subfolder" toggle.
- [ ] **T24. Best-practices / KISS audit + enforcement** — agent audit of design
  simplicity, coding standards, docs accuracy, comment quality (adversarially
  verified, then fixed); enforcement: .editorconfig + Directory.Build.props
  (warnings-as-errors locally, analyzers, EnforceCodeStyleInBuild), CODEOWNERS,
  PR/issue templates, SECURITY.md, required status checks added to the `main`
  ruleset once CI check names exist.
- [ ] **T25. First release** — tag a beta, watch the pipeline produce the GitHub
  Release (Setup.exe + portable + CLI zip), install, and verify in-app self-update
  end to end; then first stable tag.

## Deferred / follow-ups

- [ ] **T26. "Clone in WSL" recovery option** — UI verb exists in the T22 recovery
  dialog contract; actual WSL execution (wsl.exe + path translation) deferred.
- [ ] **T27. Extra package managers** — Chocolatey / Scoop manifests once releases
  are flowing.
- [ ] **T28. README screenshot** — placeholder awaits a real capture once the T23 UI
  lands.

## Needs owner action

- [ ] **O1.** Add `NUGET_API_KEY` repo secret to enable NuGet publishing of the
  engine library.
- [ ] **O2.** Add `WINGET_TOKEN` repo secret + one-time manual first winget
  submission (`wingetcreate new`); CI automates subsequent versions.
- [ ] **O3.** Enable Windows Developer Mode for packaged (F5/MSIX) debugging —
  unpackaged run and the installed Velopack build do not need it.
