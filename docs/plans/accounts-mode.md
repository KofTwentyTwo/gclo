# Implementation plan: Accounts mode + workspace redesign

> **Status: shipped.** Delivered across the v0.1.0 betas and released in
> **v1.0.0** (issues #16 and #22 closed). This document is kept as the historical
> execution plan; the tracker of record is GitHub Issues.

Delivers issue #16 (Accounts mode) built directly into the layout redesign
direction recorded on that issue, with issue #22's findings reconciled along the
way. Tracker of record: GitHub Issues; this document is the executable plan.

**Decisions already made (owner-confirmed on #16):** Windows Credential Manager
for tokens · one account = one org · Sync All · CLI parity (`--account`).
**Defaults standing unless overridden on #16:** NavigationView with Quick Sync
pinned · Sync All runs accounts sequentially · accounts remember the full
settings bundle and show a status badge · no account cap.

Every phase ends the same way: 0-warning build, all tests green, format clean,
app launch verified, gated PR `dev -> main`, and a `v0.1.0-beta.N` tag so each
increment is installable and self-update keeps getting exercised.

---

## Phase 0 — P1 accessibility & theming wave (issue #22)

Prerequisite, not part of the redesign; everything here transfers unchanged
into the new layout.

1. `StatusFormat.BrushFor` → resolve WinUI semantic brushes per call from
   `Application.Current.Resources` (`SystemFillColorSuccessBrush` Done,
   `SystemFillColorCriticalBrush` Failed, `SystemFillColorCautionBrush`
   Canceled, `SystemFillColorAttentionBrush` Cloning/Pulling, neutral Queued).
   Add `GlyphFor(SyncStatus)` returning Segoe Fluent Icons glyphs and a
   `FontIcon` beside the status text so state is never color-alone.
2. `MainWindow.xaml` inline error: `Foreground="Crimson"` →
   `{ThemeResource SystemFillColorCriticalBrush}`.
3. `AutomationProperties.LiveSetting="Polite"` on StatusText; raise UIA
   notifications for run summary and per-repo failures
   (`FrameworkElementAutomationPeer.RaiseNotificationEvent`).
4. `AutomationProperties.Name` on per-row checkboxes (repo name), select-all,
   Resolve links (repo-specific), overall ProgressBar, both ProgressRings.

Verification: build/launch; Accessibility Insights or Narrator spot-check is an
owner follow-up (headless agent cannot hear announcements). Tag `beta.5`.

## Phase 1 — Accounts foundation (libraries + CLI; no UI)

New in `gclo.ViewModels` (app-layer library, headless-testable):

- `Account` model: `Id` (guid), `Name` (unique, case-insensitive),
  `Description`, `Organization`, `TargetRoot`, `CreateOrgSubfolder`,
  `MaxConcurrency`, `LastSyncUtc`, `LastSyncSummary` (counts string).
  Metadata JSON at `%LOCALAPPDATA%\gclo\accounts.json` — never contains tokens.
- `ITokenVault` { `Store(accountId, token)`, `TryRetrieve(accountId)`,
  `Delete(accountId)` } — implementation `CredentialManagerVault` using Win32
  `CredWrite/CredRead/CredDelete` P/Invoke (generic credentials, target name
  `gclo:account:<id>`); `InMemoryVault` fake for tests. **Decision changed from
  PasswordVault after the spike**: the WinRT API would force Windows-only TFMs
  onto gclo.ViewModels and the cross-platform CLI, while the Win32 store works
  from plain net10.0 (spike-verified: write/read/delete round-trip without
  package identity) and surfaces tokens in Credential Manager's "Windows
  Credentials" UI where the owner can inspect them.
- `AccountsStore`: CRUD + rename-safe persistence (same never-throw discipline
  as `AppSettings`); deleting an account deletes its vault entry.
- CLI (`gclo.Cli`): `gclo accounts` (list: name, org, target, last sync);
  `gclo sync --account <name>` resolves registry + vault (error 2 if the vault
  entry is missing); explicit `--org/--token-*` flags override account values.
  `docs/CLI.md` section.

Tests: store CRUD/uniqueness/corrupt-file recovery; vault fake round-trip +
delete-on-remove; CLI resolution and override precedence. The real PasswordVault
is thin glue — verified by launch-time smoke, not unit tests. Tag `beta.6`.

## Phase 2 — Navigation shell

- `MainWindow` becomes a `NavigationView` (left pane): pinned "Quick Sync"
  item, accounts from `AccountsStore` (name + status dot + last-sync tooltip),
  footer "+ Add account". Content region hosts one `WorkspacePage`
  (UserControl) bound to a `WorkspaceViewModel`.
- `WorkspaceViewModel` = today's `MainViewModel` generalized: constructed from
  an `Account` (persistent) or the ephemeral Quick Sync account (today's
  behavior, nothing saved). One cached VM per account per session so switching
  accounts never loses a running sync; nav badge reflects each VM's state
  (running/ok/failed).
- Menu bar survives (File/View/Help); Settings keeps global defaults whichseed
  new accounts.

Tests: WorkspaceViewModel construction from account values; nav-badge state
mapping (pure VM). Tag `beta.7`.

## Phase 3 — Workspace redesign (the #16-comment direction)

Inside `WorkspacePage`, replacing the stacked form:

1. **Connection chip**: org · connected-state · repo count · destination +
   Edit (opens wizard for accounts; inline flyout for Quick Sync). Org-lookup
   feedback lives here — the multiplexed StatusText dies.
2. **Hero table card**: header + ListView on
   `LayerFillColorDefaultBrush`, `CornerRadius=8`; attached toolbar =
   selection summary ("12 of 87 selected"), filter box, options flyout (gear:
   parallelism, org subfolder), Refresh, and ONE accent CTA "Sync N repos".
   During a run the toolbar's right side swaps to progress (bar + "34 of 87" +
   Cancel); progress scoped to the run's selected set (fixes the #22
   progress-honesty P2).
3. **Results InfoBar** (severity-aware): summary + inline Retry failed / Open
   folder; replaces permanent buttons and the stale Retry visibility bug.
3b. **In-progress visibility** (owner decision on #16): a pinned **active
   strip** docked between toolbar and table showing exactly the repos currently
   Cloning/Pulling with per-repo progress — bounded by the parallelism setting,
   so it never scrolls and the table never moves under the user. Plus **status
   filter chips** on the toolbar (All · Active · Failed · Pending); Failed
   doubles as retry triage. Rejected: viewport auto-scroll and live re-sorting.
4. **Designed empty states**: no token → connect card (+ "how to create a
   token" link, fixing that P2); connected → "Load N repositories from {org}";
   filter-no-match state.
5. **Reconciled #22 items in this phase**: input disabling while running
   (CanEditInputs), min window size via `OverlappedPresenter`, sort-header
   accessibility, table row composed automation names, access keys +
   accelerators (Ctrl+R / Ctrl+Enter / Esc), microcopy fixes ("Parallel
   clones", placement preview wording, ellipsis, pluralization).

Tests: VM-level (selection summary, run-scoped progress, InfoBar state,
CanEditInputs); XAML verified by build + launch. Tag `beta.8`.

## Phase 4 — Account wizard + Sync All

- `AccountWizardDialog`: 1) name + description → 2) token (stored to vault,
  validated live via `IOrganizationLister`) → 3) org picker (dropdown from the
  token; manual entry fallback) → 4) destination + options (seeded from
  Settings defaults) → summary/save. Same dialog pre-filled = Edit. Delete
  confirms and removes the vault entry.
- **Sync All** (nav pane footer button): sequentially runs each account's
  WorkspaceViewModel sync; per-account rolled-up progress via nav badges;
  cancel stops the queue after the in-flight account.
- Activity log gains account context in messages (never tokens).

Tests: wizard VM validation per step; Sync All ordering/cancellation with fake
clients. Tag `beta.9`.

## Phase 5 — Close-out & release

- Reconcile remaining #22 P3s; README rewrite for accounts + new screenshots
  (issue #10 — needs owner or an interactive session for captures).
- Docs: README, CLI.md, SECURITY.md (vault storage note), RELEASING.md
  untouched.
- Owner gate: confirm self-update beta-chain works installed (#6), then tag
  **v0.1.0** stable; winget first submission (#7) follows.

---

## Risks & watch items

- **PasswordVault in unpackaged builds**: works without package identity, but
  verify early in Phase 1 on the unpackaged launch path (it is the dev loop).
  Fallback if it misbehaves: `CredRead/CredWrite` P/Invoke behind the same
  `ITokenVault`.
- **One VM per account with concurrent runs**: memory/dispatcher pressure is
  fine at ~5 accounts, but Sync All is deliberately sequential — do not let a
  future "parallel accounts" ask sneak in without revisiting disk/API limits.
- **NavigationView + MenuBar chrome**: two top-level chrome systems can fight;
  prototype the shell (Phase 2) before styling it (Phase 3).
- **Quick Sync regression risk**: it must behave exactly like today's app;
  its VM tests are the regression net.

## Execution notes

- Order is strict: 0 → 1 → 2 → 3 → 4 → 5; each phase is a separate PR through
  the required checks; no phase starts until the previous merged.
- Multi-agent builds continue to use pinned cross-agent contracts (the pattern
  that has held since the productionize wave); single-writer per file area.
- Any change to the four standing defaults happens as a comment on #16 before
  the affected phase starts.
