# gclo CLI — Git Clone Large Organizations

`gclo` is a scriptable command-line head over the same engine (`gclo.Engine`) that
powers the gclo desktop app. It clones every repository of a GitHub organization
(or user account) into a local folder, and fast-forwards the ones that already
exist there.

## Building and running

```powershell
dotnet build gclo.slnx -p:Platform=x64
.\gclo.Cli\bin\Debug\net10.0\gclo.exe --help
```

Or run it straight from the project:

```powershell
dotnet run --project gclo.Cli -- sync --org contoso --target C:\src\contoso
```

The executable is plain `net10.0` (no Windows-specific target), so it also builds
and runs on Linux/macOS hosts with the .NET 10 SDK.

## Commands

```
gclo sync --org <name> --target <folder> [--parallel N] [--sanitize-paths]
          [--include GLOB]... [--exclude GLOB]... [--skip-archived] [--dry-run]
          [--token-env VAR | --token-file PATH | --token-stdin]
          [--json | --json-lines] [--quiet]
gclo sync --account <name> [same options — each one overrides the account value]
gclo repos --org <name> [--include GLOB]... [--exclude GLOB]... [--skip-archived]
          [--token-env VAR | --token-file PATH | --token-stdin] [--json]
gclo orgs [--token-env VAR | --token-file PATH | --token-stdin] [--json]
gclo accounts [list] [--json]
gclo accounts add --name <name> --org <name> --target <folder> [--parallel N]
          [--org-subfolder] [--description TEXT] [token option]
gclo accounts edit --name <name> [--rename NEW] [--org NAME] [--target FOLDER]
          [--parallel N] [--org-subfolder | --no-org-subfolder] [--description TEXT]
          [token option]
gclo accounts remove --name <name>
gclo --version
gclo --help            (each command also accepts --help; 'gclo help sync' works too)
```

Options may be written as `--name value` or `--name=value`.

### `gclo sync`

Clones every repository of `--org` into `<target>\<repo>`. Repositories that are
already valid git repositories locally are fetched and fast-forwarded instead.
Repositories fail independently — one failure never stops the rest. Up to
`--parallel` git operations run at once (default 8, maximum 64 — the same
ceiling the desktop app applies, because more would trip GitHub's secondary
rate limit).

`--org` accepts an organization login or a user account login. If the name is
an organization the token cannot see, the run fails with exit code 3 and a hint
to grant the token access (an older build would silently sync only the
organization's public repositories). If the name is a user account, gclo lists
that account's repositories (your own account includes private repos the token
can see).

#### Selecting repositories

The desktop app lets you list, pick, and sync a subset; the CLI has the same
shape:

| Option | Effect |
| --- | --- |
| `--include GLOB` | Only repositories whose **whole name** matches the pattern (`*` and `?` wildcards, case-insensitive). Repeatable; a repository is selected when any include matches. |
| `--exclude GLOB` | Drop repositories whose name matches. Repeatable; applied after `--include`. |
| `--skip-archived` | Drop archived repositories. |
| `--dry-run` | Print what the run would do (`<name>  would clone` / `would update`), then exit 0 without touching the network beyond the listing or the disk at all. With `--json` or `--json-lines` the dry run prints a JSON array `[{"repo":"...","action":"clone"|"update"}]`. |

`gclo repos` (below) previews the same selection without a target folder.

The summary counts the **selection**, not the listing: `--include platform-*`
over a 400-repository organization with 15 matches reports `of 15`.

#### Progress output

One line per repository status transition:

```
gclo  Queued
gclo  Cloning
gclo  Done
old-repo  Pulling
broken-repo  Failed  remote authentication failed
```

- Non-failure lines (`Queued`, `Cloning`, `Pulling`, `Done`, `Canceled`) go to
  **stdout**; `Failed` lines go to **stderr** so they survive redirection.
- Clone percentage updates are never printed — only transitions.
- `--quiet` suppresses the stdout progress lines; failures (stderr) and the final
  summary still print.
- A summary line always ends the run:
  `Finished: 3 cloned, 41 updated, 1 failed, 0 canceled of 45.`

**Ctrl+C** cancels gracefully: in-flight git operations stop, remaining
repositories are marked `Canceled`, and the summary still prints. A second
Ctrl+C aborts the process immediately.

#### Windows-invalid paths and `--sanitize-paths`

git happily stores paths that no Windows file system can hold: reserved device
names (`aux`, `con`, `nul`, `com1`, ...), characters like `:`, `?`, or `*`,
names ending in a dot or a space, and pairs of paths that differ only by case.
gclo validates every incoming tree *before* it touches the working tree, so such
a repository fails cleanly — nothing is half-checked-out — and the offending
paths are listed on stderr (at most 10, plus a count of the rest). Each line
reports the path of the offending *segment* itself — a bad directory name like
`aux` is reported once as `aux`, not repeated for every file inside it:

```
legacy-repo  Failed  2 paths in this repository cannot be created on Windows: ...
legacy-repo    aux  ('aux' is a reserved Windows device name)
legacy-repo    docs/spec?.md  (contains a character that is invalid on Windows)
```

A repository whose **name** is itself a reserved Windows device name (an
organization really can own a repository called `aux`) fails its own row with
a plain reason before anything is cloned; rename it on GitHub or sync it on
another platform.

This validation — like the automatic `core.longpaths` handling — applies on
Windows only: on Linux and macOS these paths are perfectly legal, so gclo
performs a plain checkout and `--sanitize-paths` has nothing to do.

With `--sanitize-paths`, gclo checks such a repository out anyway:

- Each offending path is renamed on disk to a safe suggested name
  (`aux` → `aux_`, `spec?.md` → `spec_.md`).
- Paths with no safe automatic rename (case-only collisions) are **skipped**:
  they are not materialized on disk.
- A note listing the renames goes to stderr (text modes) and to the activity
  log, and the repository counts as **cloned** — the exit code stays `0`. The
  human summary calls the count out:
  `Finished: 3 cloned (1 with sanitized paths), 41 updated, 0 failed, 0 canceled of 45.`
  The JSON document lists each sanitized repository with its skipped paths
  (see below), so a pipeline can tell an incomplete working tree from a
  complete one.
- The mapping is remembered inside the repository
  (`.git\gclo-recovery.json`) and reapplied by later syncs — which also remove
  files that have since left the tree and leave the tree untouched when the
  upstream tip has not moved. A later sync that brings *new* invalid paths not
  covered by the stored mapping fails with those paths listed — sync again with
  `--sanitize-paths` (or use the GUI's **Resolve…** dialog) and the new renames
  and skips are merged into the stored mapping. Use `--include <repo>` to
  target just that repository.

Only the working tree is renamed — the repository's history and index still hold
the original paths, so `git status` reports the renamed and skipped files as
local changes. Treat a sanitized repository as a read-only mirror.

#### `--json`

Suppresses all progress lines and prints a single line of JSON to stdout when the
run ends (also on cancellation):

```json
{"type":"summary","total":45,"cloned":3,"updated":41,"failed":1,"canceled":0,"wasCanceled":false,
 "failures":[{"repo":"broken-repo","error":"remote authentication failed","invalidPaths":null},
             {"repo":"legacy","error":"2 paths in this repository cannot be created on Windows: ...",
              "invalidPaths":[{"path":"aux","reason":"'aux' is a reserved Windows device name","suggestedName":"aux_"}]}],
 "sanitized":[{"repo":"fixed","renamed":3,"skipped":1,"skippedPaths":["docs/Readme.md"]}]}
```

- `type` is always `"summary"`.
- `failures[].invalidPaths` is the **full** list of offending paths when the
  failure was Windows path validation (the message itself only quotes the
  first three); `null` for any other failure.
- `sanitized[]` lists the repositories `--sanitize-paths` checked out with
  renames and skips; they are counted under `cloned`, but `skippedPaths` are
  not on disk.
- Nothing else is printed to stdout, and nothing to stderr: the document is the
  whole result. A fatal exit (codes 2 and up) prints **no** document — check
  the exit code before parsing.

#### `--json-lines`

Prints one JSON object per status transition to stdout as it happens (NDJSON),
then the same summary object as `--json` as the last line:

```json
{"type":"progress","repo":"gclo","status":"Queued","error":null}
{"type":"progress","repo":"gclo","status":"Cloning","error":null}
{"type":"progress","repo":"broken-repo","status":"Failed","error":"remote authentication failed"}
{"type":"progress","repo":"gclo","status":"Done","error":null}
{"type":"summary","total":2,"cloned":1,...}
```

Clone percentage updates are not emitted. `--json` and `--json-lines` are
mutually exclusive.

### `gclo repos`

Lists the repositories a sync would process — one per line: name, default
branch (`-` for an empty repository), and `archived` when the repository is
archived — sorted by name. The same `--include`, `--exclude`, and
`--skip-archived` filters as `gclo sync` apply, so it previews a selection:

```
$ gclo repos --org contoso --include "platform-*" --skip-archived
platform-api   main
platform-web   main
platform-old   master  archived
```

With `--json` it prints a single-line JSON array:

```json
[{"name":"platform-api","defaultBranch":"main","archived":false,"cloneUrl":"https://github.com/contoso/platform-api.git"}]
```

An empty selection prints a hint on stderr and still exits 0.

### `gclo orgs`

Prints the logins the token can sync, one per line: the token's own account login
first, then its organizations alphabetically. With `--json` it prints a
single-line JSON array instead (e.g. `["octocat","contoso","fabrikam"]`).

A token that cannot list organizations (a fine-grained PAT, or a classic PAT
without the `read:org` scope) still prints its own account login — you can pass
any organization name to `gclo sync` manually. A rate limit or temporary
throttle is reported as such (exit code 4) rather than shrinking the list.

## Accounts

An *account* is a saved sync profile: a name, an organization, a target root
folder, a parallelism setting, an org-subfolder preference, an optional
description, and a token stored in **Windows Credential Manager** (under
`gclo:account:<id>`; the metadata file, `%LOCALAPPDATA%\gclo\accounts.json`,
never contains the token). Accounts are shared between the CLI and the desktop
app: create them with `gclo accounts add` on a headless mirror server, or in
the app's account wizard, and use them from either.

> **Windows-only.** Because the token lives in Windows Credential Manager,
> `gclo accounts` and `gclo sync --account` exit with code 2 on Linux/macOS.
> Plain `gclo sync`, `gclo repos`, and `gclo orgs` work everywhere.

### `gclo accounts` / `gclo accounts list`

Lists the saved accounts, one per line, in aligned columns: name, organization,
target root, and last sync time (local time, `never` when the account has not
completed a sync yet):

```
work      contoso   C:\src\contoso   2026-07-04 09:12
personal  octocat   D:\mirror        never
```

With `--json` it prints a single-line JSON array with everything a script needs
to reason about a run; `lastSync` is the UTC timestamp of the last completed
sync and `lastSyncSummary` its summary line, both `null` until the first sync:

```json
[{"id":"d0f0e0c09c404a5e8f3a5b1e6f7a2c11","name":"work","description":"","organization":"contoso","targetRoot":"C:\\src\\contoso","createOrgSubfolder":true,"maxConcurrency":8,"lastSync":"2026-07-04T14:12:03+00:00","lastSyncSummary":"Finished: 3 cloned, 41 updated, 0 failed, 0 canceled of 44."}]
```

`id` is the key of the account's Credential Manager entry (`gclo:account:<id>`).
When no accounts exist yet, stdout stays empty (`--json` prints `[]`) and a
hint goes to stderr; the exit code is still 0.

### `gclo accounts add`

```powershell
gh auth token | gclo accounts add --name work --org contoso --target C:\src\contoso --org-subfolder --parallel 16 --token-stdin
```

`--name`, `--org`, and `--target` are required; `--parallel` (1-64, default 8),
`--org-subfolder`, and `--description` are optional. The token comes from a
token option exactly as for `gclo sync` (default: the `GITHUB_TOKEN` environment
variable) and is written to Windows Credential Manager — never put it on the
command line. A name that another account already uses (case-insensitive)
exits with code 2.

### `gclo accounts edit`

```powershell
gclo accounts edit --name work --parallel 24 --no-org-subfolder
gh auth token | gclo accounts edit --name work --token-stdin     # rotate the stored token
gclo accounts edit --name work --rename "work (contoso)"
```

`--name` picks the account; every other option replaces just that setting.
Without a token option the stored token is left untouched. Editing with
nothing to change is a usage error.

### `gclo accounts remove`

```powershell
gclo accounts remove --name work
```

Deletes the account's settings and its Credential Manager entry. Repositories
on disk are not touched.

### `gclo sync --account <name>`

Runs a sync with the account's settings and its stored token — no `--org`,
`--target`, or token option needed:

```powershell
gclo sync --account work
```

Account values are **defaults**; any explicit option overrides them:

| Setting | Account value | Overridden by |
| --- | --- | --- |
| Organization | the account's organization | `--org` |
| Target folder | the account's target root — plus an `\<organization>` subfolder when the account opts into one | `--target` (used verbatim; no subfolder is appended) |
| Parallelism | the account's max concurrency | `--parallel` |
| Token | the Windows Credential Manager entry | `--token-env`, `--token-file`, or `--token-stdin` |

Notes:

- When the account opts into an organization subfolder and you override
  `--org`, the subfolder follows the *effective* organization:
  `<targetRoot>\<org>`.
- When the run completes (exit code 0 or 1, including a canceled run), the
  time and the summary line are recorded on the account — `gclo accounts` and
  the desktop app show them as the last sync.
- An unknown account name exits with code 2 and lists the available account
  names on stderr.
- An account whose Credential Manager entry is missing (deleted, or the
  profile moved to another machine — the entry does not roam) exits with
  code 2. Re-enter the token with `gclo accounts edit --name <name> --token-stdin`
  or in the desktop app's account wizard, restore the `gclo:account:<id>`
  credential manually, or pass a token option for this run.

## Providing the token

gclo needs a GitHub Personal Access Token for both the API and the git transport.

> **Why is there no `--token <value>` option?**
> Command-line arguments are visible to every other process on the machine —
> Task Manager, `ps`, `wmic process`, `/proc/<pid>/cmdline` — and often end up in
> shell history and logs. A token passed as a plain argument would leak to every
> local user and program. gclo therefore only accepts tokens through channels
> that stay off the command line.

| Option | Behavior |
| --- | --- |
| *(none)* | Reads the `GITHUB_TOKEN` environment variable. |
| `--token-env VAR` | Reads environment variable `VAR`. |
| `--token-file PATH` | Reads the first non-blank line of `PATH`, trimmed. |
| `--token-stdin` | Reads one line from standard input — made for piping from a secret store. |

The options are mutually exclusive. A missing or empty token prints an error to
stderr and exits with code 2.

With `gclo sync --account`, the default source is the account's token in
Windows Credential Manager instead of `GITHUB_TOKEN`; any token option above
still wins for that run (the stored token is left untouched).

## Activity log

Every invocation appends to a daily activity log file, `gclo-yyyy-MM-dd.log`,
under `%LOCALAPPDATA%\gclo\logs` (on Linux/macOS: the platform's local
application data folder). It records the run's parameters, per-repository
failures, and path-sanitization notes — the same log the desktop app shows
under **View > Activity log**. Tokens are never written to the log, or anywhere
else on disk; see [SECURITY.md](../SECURITY.md).

## Exit codes

| Code | Meaning | What to do |
| --- | --- | --- |
| 0 | Everything succeeded (or `--dry-run` / a listing completed). | — |
| 1 | The run completed, but some repositories failed or the run was canceled (Ctrl+C). `--json`'s `wasCanceled` tells the two apart. | Inspect the failures; retry them. |
| 2 | Fatal: bad arguments, missing/empty token, organization not found, unknown account, missing account token, unusable target folder, or an account command on a non-Windows OS. | Fix the invocation. |
| 3 | GitHub rejected the token (401) or the token may not see the organization (403). | Fix the token or its access; do not retry as is. |
| 4 | GitHub's primary or secondary rate limit, or a temporary throttle. | Retry later (the message says when). |
| 70 | An unexpected error inside gclo. The exception type is printed and the full details are in the activity log. | Please report it. |

Codes 3 and 4 were part of code 2 before version 1.1; a script that tested
`-eq 2` for "usage error" should treat `3` and `4` as distinct outcomes.

A repository recovered by `--sanitize-paths` counts as a success: if every other
repository also succeeds, the exit code is `0`. With `--json` or `--json-lines`,
exits 2 and up print no document on stdout.

## Examples

### PowerShell

```powershell
# Default token source: the GITHUB_TOKEN environment variable
$env:GITHUB_TOKEN = (Get-Secret -Name GitHubPat -AsPlainText)   # SecretManagement module
gclo sync --org contoso --target C:\src\contoso

# A differently named environment variable
gclo sync --org contoso --target C:\src\contoso --token-env GH_WORK_TOKEN

# Token stored in a file (keep it out of the repo and readable only by you)
gclo sync --org contoso --target C:\src\contoso --token-file $HOME\.config\gclo\token

# Pipe the token from a secret store — it never touches a command line or disk
Get-Secret -Name GitHubPat -AsPlainText | gclo orgs --token-stdin
op read "op://Private/GitHub PAT/token" | gclo sync --org contoso --target C:\src\contoso --token-stdin
gh auth token | gclo sync --org contoso --target C:\src\contoso --token-stdin

# Preview, then sync a subset
gclo repos --org contoso --include "platform-*" --skip-archived
gclo sync --org contoso --target C:\src\contoso --include "platform-*" --skip-archived --dry-run
gclo sync --org contoso --target C:\src\contoso --include "platform-*" --skip-archived

# Machine-readable result
$result = gclo sync --org contoso --target C:\src\contoso --json | ConvertFrom-Json
if ($result.failed -gt 0) { $result.failures | ForEach-Object { "$($_.repo): $($_.error)" } }
$result.sanitized | ForEach-Object { "$($_.repo): skipped $($_.skippedPaths -join ', ')" }

# Live progress for a dashboard
gclo sync --org contoso --target C:\src\contoso --json-lines | ForEach-Object { $_ | ConvertFrom-Json }

# Nightly mirror job: quiet, branch on the exit code
gclo sync --org contoso --target D:\mirror\contoso --parallel 16 --quiet
switch ($LASTEXITCODE) {
  0 { }
  1 { Write-Warning "some repositories failed" }
  4 { Write-Warning "rate limited; will retry next run" }
  default { Write-Error "sync ended with code $LASTEXITCODE" }
}

# Legacy repos with Windows-invalid paths: rename/skip them instead of failing
gclo sync --org contoso --target C:\src\contoso --sanitize-paths

# Saved accounts (Windows only), created without the desktop app
gh auth token | gclo accounts add --name work --org contoso --target C:\src\contoso --org-subfolder --token-stdin
gclo accounts                              # name, organization, target root, last sync
gclo sync --account work                   # settings and token come from the account
gclo sync --account work --parallel 16     # explicit options override account values
gclo sync --account work --org other-org   # same target root, different organization
gclo accounts edit --name work --parallel 24
gclo accounts remove --name work

# Nightly job over every account
gclo accounts --json | ConvertFrom-Json | ForEach-Object {
  gclo sync --account $_.name --quiet
  if ($LASTEXITCODE -ne 0) { Write-Error "sync of '$($_.name)' ended with code $LASTEXITCODE" }
}
```

### bash

```bash
# Default token source: the GITHUB_TOKEN environment variable
export GITHUB_TOKEN="$(pass show github/pat)"
gclo sync --org contoso --target ~/src/contoso

# Token file
gclo sync --org contoso --target ~/src/contoso --token-file ~/.config/gclo/token

# Pipe the token from a secret store
pass show github/pat | gclo sync --org contoso --target ~/src/contoso --token-stdin
op read "op://Private/GitHub PAT/token" | gclo orgs --token-stdin
gh auth token | gclo sync --org contoso --target ~/src/contoso --token-stdin

# Preview, then sync a subset
gclo repos --org contoso --include 'platform-*' --skip-archived --json | jq -r '.[].name'
gclo sync --org contoso --target ~/src/contoso --exclude '*-archive' --dry-run

# Machine-readable result with jq
gclo sync --org contoso --target ~/src/contoso --json |
  jq -r '.failures[] | "\(.repo): \(.error)"'

# Live progress as NDJSON
gclo sync --org contoso --target ~/src/contoso --json-lines |
  jq -r 'select(.type == "progress") | "\(.repo) \(.status)"'

# Cron-friendly: quiet progress, failures on stderr, exit code drives alerting
gclo sync --org contoso --target /srv/mirror/contoso --parallel 16 --quiet
case $? in
  0) ;;
  1) echo "some repositories failed" ;;
  4) echo "rate limited; retrying next run" ;;
  *) echo "sync ended with code $?" >&2 ;;
esac

# List orgs as JSON and sync each one
for org in $(gclo orgs --json | jq -r '.[]'); do
  gclo sync --org "$org" --target ~/src/"$org" --quiet
done
```

## Version

```console
$ gclo --version
1.0.0 (a893741d2)
```

Released builds print the semantic version and the short commit hash; local builds print the next-release version with a `-dev` suffix (e.g. `1.0.1-dev (<hash>)`).
