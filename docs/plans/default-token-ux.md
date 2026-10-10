# UX plan: the default token and account tokens

> **Status: approved** by the owner on 2026-10-10; step 1 is #104. Delivers #101
> (Settings shows a saved default token as a masked value) and #102 (Add account
> uses the default token by reference). Tracker of record: GitHub Issues; this
> document is the design.

## The problem, as the owner sees it

1. In **Settings**, a saved default token leaves the token box empty. An empty box
   reads as "nothing saved", even with the caption underneath.
2. **Add account** always demands a token on step 2, even when a default token is
   saved. Every account then holds its own copy, so rotating the default token in
   Settings does nothing for existing accounts: each keeps the old copy until it
   fails.

## Design decisions

- **An account references the default token; it never copies it.** A new field
  `TokenSource` on the account record: `Default` (use whatever the default token
  is right now) or `Own` (this account's own vault entry, today's behaviour).
  Rotating the default token in Settings rotates every `Default` account at once.
  `Own` accounts are untouched by the default, as today.
- **One resolver, used everywhere.** `AccountTokenResolver.Resolve(account)` in
  `gclo.ViewModels` returns the token to use for an account (`Default` → vault
  entry `DefaultTokenVaultId`; `Own` → vault entry `account.Id`) plus *why* it is
  missing when it is. The desktop workspace, Sync All, the wizard's validation
  and the CLI's `sync --account` all go through it. No consumer reads the vault
  for an account directly any more.
- **The token itself is never shown.** The Settings box shows a fixed mask when a
  token is saved; the wizard never loads a token into a box; the reveal button
  of a `PasswordBox` can only ever reveal what the user typed in this session.
- **Backward compatible on disk.** `TokenSource` defaults to `Own` when absent, so
  every existing `accounts.json` and every existing vault entry keeps working.
  1.0.x builds reading a newer file ignore the field (System.Text.Json default).
- **Deleting the default token is a guarded action** when `Default` accounts
  exist: Settings says which accounts stop working and asks for confirmation.

## Settings dialog (#101)

| State | Token box | Caption | Remove link |
| --- | --- | --- | --- |
| No default token saved | empty, placeholder `ghp_…` | "No default token saved. Quick Sync will ask for one each launch." | hidden |
| Default token saved, untouched | shows the mask `••••••••••••••••` (a constant, never the token) | "A default token is saved and used by Quick Sync and *N* account(s). Type to replace it." | visible |
| User typed a new value | the typed value (masked) | "The default token will be replaced on Save." | visible |
| User cleared the box, or pressed Remove | empty | "The default token will be removed on Save. *N* account(s) use it and will stop syncing until a new one is saved." (plain "…will be removed on Save." when N = 0) | hidden |

Save semantics: mask unchanged → keep; non-empty and not the mask → store; empty
→ remove (only if one was saved). Removing while `Default` accounts exist shows a
confirmation ("Remove the default token? These accounts use it: A, B.") before the
dialog closes; Cancel returns to the dialog with the mask restored.

Accessibility: the box keeps its automation id `SettingsDefaultTokenBox`; the
mask is announced as the field's value by the password pattern (dots), the
caption is a live region, and the Remove link's text names the consequence.

## Add account / Edit account wizard (#102)

Step 2 ("Access token") becomes a choice, shown only when a default token is
saved; without one the step looks exactly as today.

```text
Step 2 of 4: Access token

( ) Use the default token from Settings      ← selected by default for a new account
    Used by Quick Sync and N other account(s). Replacing it in Settings updates
    this account too.
( ) Use a different token for this account
    [ Personal access token ..................... ]  (enabled only when selected)
    How to create a token

Next validates the chosen token and lists the organizations it can see.
```

- **New account:** "Use the default token" is preselected. Next validates the
  *default* token through the resolver and lists its organizations. On save the
  account is written with `TokenSource = Default` and **no vault entry** of its
  own. Choosing "a different token" requires typing one; save writes
  `TokenSource = Own` and the entry, as today.
- **Edit account:** the radio reflects the current source. Switching `Own →
  Default` deletes the account's own vault entry on save (confirmed in the
  caption: "This account's own token will be removed from Credential Manager").
  Switching `Default → Own` requires a token. Staying on `Own` with the box
  empty keeps the stored token, as today.
- **Default token missing** (removed after the account was created): step 2
  shows "The default token is no longer saved. Save one in Settings, or use a
  different token for this account." and Next stays put.
- **Seeded wizard** ("save this Quick Sync connection as an account"): if the
  typed Quick Sync token equals the saved default token, the wizard preselects
  "Use the default token"; otherwise "a different token" with the typed value.
  (Comparison happens in memory in the view model; nothing is logged.)

## Where the account's token shows up elsewhere

- **Navigation pane / account header:** `Default` accounts get a small
  "Default token" tag (text, not just an icon) in the account's header, so the
  dependency on Settings is visible where the account is used.
- **Account workspace:** the token box is prefilled from the resolver as today.
  For a `Default` account its header reads "Using the default token"; editing the
  box affects this session only, exactly as for Quick Sync.
- **Sync All:** a `Default` account whose default token is missing is skipped
  with the result "No default token saved (Settings)" instead of a generic auth
  error.
- **Error messages:** every "no token" message names the source
  ("the default token in Settings" or "this account's own token") and the fix.

## CLI parity (`gclo accounts`, `gclo sync --account`)

- `accounts add … --use-default-token` creates a `Default` account; the existing
  `--token-env | --token-file | --token-stdin` create `Own` accounts. Exactly one
  of the four is required (today: one of the three).
- `accounts edit --name X --use-default-token` switches to `Default` and removes
  the own entry; a `--token-*` option switches to `Own`.
- `accounts list` gains a `token` column: `default` or `own`; the JSON output
  gains `"tokenSource"`.
- `sync --account X` resolves through the resolver; its "no token" error names
  the source and the fix (`gclo accounts edit --name X --token-stdin`, or saving
  a default token in the desktop app's Settings; the CLI gets no way to *set*
  the default token in this change).
- The default token stays a desktop-app concept to set, but the CLI reads it,
  so a headless mirror can use `Default` accounts created on the same machine.

## Data model and storage

- `Account.TokenSource` (`enum TokenSource { Own, Default }`), serialized as a
  string, default `Own`, `[JsonIgnoreCondition.WhenWritingDefault]` so existing
  files stay byte-identical until an account actually uses the default.
- `AccountsStore.Save(account, token)` keeps its contract; a `Default` account is
  always saved with `token: null`, and switching to `Default` removes the own
  entry inside the same vault-first/compensate sequence the store already has.
- `AccountsStore.CountUsingDefaultToken()` feeds the Settings captions.
- Vault scoping (#60) is unchanged: `DefaultTokenVaultId` already lives in the
  same scope as the accounts.

## Security review (threat model T5, T6)

- No new place holds a token: the mask is a constant; the resolver returns the
  token to the same callers that already received it; nothing is logged.
- A `Default` account widens the blast radius of the default token to every
  such account by design; the Settings warning on removal and the "Default
  token" tag make that dependency visible. Rotation becomes a single action,
  which is the point.
- The CLI still has no `--token <value>`; `--use-default-token` carries no
  secret on the command line.

## Tests

- ViewModels (100% line coverage stays enforced): resolver (both sources,
  missing cases), wizard step 2 for new/edit/seeded in both sources and the
  missing-default error, store save/switch/compensation with `TokenSource`,
  settings captions and remove-with-dependents counts (through a small
  `SettingsTokenState` view model so the dialog has no logic of its own),
  Sync All skip reason.
- CLI (100%): `--use-default-token` on add/edit, mutual exclusion with
  `--token-*`, list column and JSON field, sync resolution error text.
- FlaUI: Settings shows the mask when a token is saved and an empty box when
  not; Add account shows the radio only when a default token exists, and the
  token box is disabled while "Use the default token" is selected.
- Serialization round-trip: an `accounts.json` without the field loads as `Own`
  and re-saves without the field.

## Delivery

One issue per layer, one PR each, in this order, every PR green on all gates:

1. **#102a – model + resolver + store + CLI** (no UI change yet; `Default`
   accounts can only be created by the CLI so far). Threat model updated.
2. **#102b – wizard + pane tag + Sync All reason** (desktop).
3. **#101 – Settings dialog** (mask, captions, guarded removal), last because its
   "used by N accounts" count depends on step 1.

Ships in the next minor (1.1.0): it adds a field to the account record and a
CLI option, which is a feature, not a fix; 1.0.2 stays the pipeline/data-root
fix release.
