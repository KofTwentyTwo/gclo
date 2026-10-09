# ADR-0002: Build the desktop app on WinUI 3 and keep it runnable unpackaged

- **Status:** Accepted
- **Date:** 2026-07-05 (recorded 2026-10-09)
- **Pull request:** #64

## Context

The desktop app needs a modern Windows 11 look, accessibility through UI Automation
(the FlaUI end-to-end suite and screen readers depend on it), and a packaging model
that works for a self-updating per-user install (ADR-0001). The .NET UI choices are
WinUI 3 (Windows App SDK), WPF with the Fluent theme, and cross-platform frameworks.

## Options considered

### Option A: WPF with the Fluent theme

- Pros: mature, large control set, runs unpackaged trivially.
- Cons: Fluent theme fidelity lags WinUI; no Mica/Acrylic backdrops without extra work.

### Option B: Avalonia or Uno

- Pros: cross-platform.
- Cons: the macOS edition is a separate native project (#11, #48); a cross-platform
  UI layer adds a dependency without a second platform to serve here.

### Option C: WinUI 3 (Windows App SDK), buildable both packaged and unpackaged

- Pros: current Windows UI, UIA peers for every control, Mica backdrop, Windows App
  SDK self-contained deployment; unpackaged builds suit Velopack and the test harness.
- Cons: packaged F5 debugging needs Developer Mode; some APIs require package identity
  and must be avoided or guarded.

## Decision

WinUI 3 (Option C). Every build must also run unpackaged (`WindowsPackageType=None`,
`WindowsAppSDKSelfContained=true`); APIs that need package identity are not used
without a fallback. Paths come from `Environment.GetFolderPath`, not
`Windows.Storage.ApplicationData`, so the same code works in both modes.

## Consequences

- CI builds unpackaged and runs the FlaUI suite against that exe; the release ships
  the unpackaged publish wrapped by Velopack.
- Third-party controls must expose UI Automation (see WinUI.TableView and the
  `AccessibleTableView` shim added for #33).
- Security: no change to the attack surface; the app runs as the user with no
  package-identity privileges.
- Follow-up: none.
