# gclo CLI — Git Clone Large Organizations

`gclo` is a scriptable command-line head over the same engine (`gclo.Engine`) that
powers the gclo desktop app. It clones every repository of a GitHub organization
(or user account), or of one on a Gitea server, into a local folder, and
fast-forwards the ones that already exist there.

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
          [--forge github|gitea] [--server ADDRESS]
          [--include GLOB]... [--exclude GLOB]... [--skip-archived] [--dry-run]
          [--token-env VAR | --token-file PATH | --token-stdin]
          [--json | --json-lines] [--quiet]
gclo sync --account <name> [same options — each one overrides the account value;
          not --forge or --server: the account says where it lives]
gclo repos --org <name> [--forge github|gitea] [--server ADDRESS]
          [--include GLOB]... [--exclude GLOB]... [--skip-archived]
          [--token-env VAR | --token-file PATH | --token-stdin] [--json]
gclo orgs [--forge github|gitea] [--server ADDRESS]
          [--token-env VAR | --token-file PATH | --token-stdin] [--json]
gclo accounts [list] [--json]
gclo accounts add --name <name> --org <name> --target <folder> [--parallel N]
          [--forge github|gitea] [--server ADDRESS]
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
When a repository gets its first checkout into a folder that already holds something
(it was empty when it was cloned, or its first checkout was interrupted), gclo on
Windows replaces a file there only if it is what its own unfinished checkout wrote;
otherwise that repository fails and the message names what is in the way.
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
Ctrl+C aborts the process immediately. So does the first one while gclo waits
for the token at its prompt (`--token-stdin` in a terminal), where nothing has
been started yet. In `gclo accounts`, the first Ctrl+C lets a change that is
under way finish (it writes to the credential store and to the accounts file,
and is not cut off between the two). A `--dry-run` that is canceled while the
repositories are being listed prints no plan, and with `--json` or
`--json-lines` no document, and exits 1.

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
  (`aux` → `aux_`, `com3.txt` → `com3_.txt`, `spec?.md` → `spec_.md`).
- Paths with no safe automatic rename are **skipped**: they are not
  materialized on disk. That covers case-only collisions, and paths whose
  suggested names would collide with each other (`a:b` and `a?b` would both
  become `a_b`; two folders in that position are both skipped, not merged).
  A suggested name that collides with a path the repository already has
  (`a:b` beside an existing `a_b`) is not detected up front: that repository
  fails with the colliding path named, and nothing is checked out.
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
  and skips are merged into the stored mapping; a newer choice for a path
  replaces the stored one. Use `--include <repo>` to target just that
  repository.
- A repository that was already checked out normally is never rewritten this
  way. When an update brings such a repository its first invalid path, it
  fails with those paths listed; with `--sanitize-paths` the failure says
  that the repository is checked out normally, and nothing in it is changed:
  its working tree may hold work, and writing it by hand could destroy that.
  To get it with the renames, move or delete its folder and sync again (the
  fresh clone is then sanitized), or use the desktop app's **Clone in WSL**.
  The same holds when the paths of a clone that was never checked out, or of
  an empty one, are resolved and files were put into its folder by hand: a file
  that is already where the repository has one, and is not what an unfinished
  checkout would have left there, is not replaced by the renaming.

Only the working tree is renamed — the repository's history and index still hold
the original paths, so `git status` reports renamed and skipped paths as local
changes. Treat a sanitized repository as a read-only mirror: later syncs
rewrite its working tree, so edits made there are overwritten. A commit made
there is not dropped: while origin has nothing new, a sync leaves the tree as
it is, and once origin has moved on as well the sync fails for that
repository, as it does for any clone with local commits. The branch is only
ever moved to the tip of the branch it tracks: forward, or, when origin's
history was rewritten, off a commit that this clone's record of earlier
fetches shows was origin's own.

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
  first three), also when `--sanitize-paths` then could not fix the repository
  (`error` says why); `null` for any other failure.
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

### GitHub, or a Gitea server: `--forge` and `--server`

Without either option a command talks to GitHub. For an organization on a Gitea
server (or a Forgejo server, which speaks the same API), name the server:

```powershell
$env:GITEA_TOKEN = (Get-Secret -Name GiteaToken -AsPlainText)
gclo orgs  --server https://git.example.com
gclo repos --server https://git.example.com --org platform
gclo sync  --server https://git.example.com --org platform --target D:\mirror\platform
```

- `--server <address>` is the server's own address. `--forge gitea` goes with it
  and may be left out; `--forge github` is the default and takes no server.
- **HTTPS only.** `http://` is refused, and nothing turns certificate checks
  off. A server with a certificate from a private authority works once Windows
  (or the system gclo runs on) trusts that authority.
- **The token goes to that server and nowhere else.** gclo does not follow a
  redirect the server answers with, and clones from the address you gave, not
  from an address the server names. If the server answers with a redirect, the
  address is not the one the server itself uses, or the organization was
  renamed; the message says so.
- **Each forge has its own default token variable**: `GITHUB_TOKEN` for GitHub,
  `GITEA_TOKEN` for a Gitea server. One is never used for the other, so a GitHub
  token that happens to be in the environment is not sent to a Gitea server.
  `--token-env`, `--token-file` and `--token-stdin` work as everywhere.
- A Gitea access token needs to be allowed to read repositories, organizations
  and the user (`read:repository`, `read:organization`, `read:user`). Without
  the second, `gclo orgs` prints the account alone.
- A server that is reachable only through a sign-in page or a single-sign-on
  proxy does not work: gclo reports that the address does not answer like a
  Gitea server.

## Accounts

An *account* is a saved sync profile: a name, an organization, where it lives
(GitHub, or a Gitea server and its address), a target root folder, a parallelism
setting, an org-subfolder preference, an optional description, and a token
stored in **Windows Credential Manager** (under `gclo:account:<id>`, or
`gclo:<scope>:account:<id>` when `GCLO_DATA_DIR` points the data elsewhere; the
metadata file, `%LOCALAPPDATA%\KofTwentyTwo\gclo\accounts.json`, never contains
the token).

Accounts on a Gitea server are kept in a file of their own beside it,
`accounts-forges.json`. A version of gclo from before 1.2 knows only GitHub: it
reads every account in `accounts.json` as a GitHub account and writes the file
back with what it knows. It neither reads nor writes the other file, so it does
not list such accounts, and using it beside 1.2 leaves them as they are. One
thing it can do: add a GitHub account with the name of an account it does not
see. gclo then does not choose between the two: `--account <name>`,
`accounts edit` and `accounts remove` fail (exit 2) and say that two accounts
have the name, until one of them is renamed in the desktop app.

The token of such an account is kept under an entry name made from the account's
id, its forge and its server address as the file states them, not under `<id>`.
It is found only by that account at that address: when the address is changed in
the file by hand, the account has no token there, instead of sending the one it
had to the new address.
Accounts are shared between the CLI and the desktop app: create them with
`gclo accounts add` on a headless mirror server, or in the app's account
wizard, and use them from either.

The CLI and the app can run at the same time, and so can several CLI runs:
each change (adding, editing, or removing an account, recording a sync
result) is applied to what the file holds at that moment. An account one of
them added or removed stays added or removed, and a recorded sync result is
kept; when two of them edit the same account, the later edit wins, and an
edit of an account that was removed in the meantime is refused. The app lists
an account that was added from the CLI the next time it starts. If another
gclo process is changing the accounts at that very moment, a command waits a
few seconds and then exits with code 4; run it again.

> **Windows-only.** Because the token lives in Windows Credential Manager,
> `gclo accounts` and `gclo sync --account` exit with code 2 on Linux/macOS.
> Plain `gclo sync`, `gclo repos`, and `gclo orgs` work everywhere.

### `gclo accounts` / `gclo accounts list`

Lists the saved accounts, one per line, in aligned columns: name, organization,
where it lives (`GitHub`, or the host of a Gitea server), target root, token
source (`own` or `default`), and last sync time (local time, `never` when the
account has not completed a sync yet):

```
work      contoso   GitHub           C:\src\contoso   own      2026-07-04 09:12
personal  octocat   GitHub           D:\mirror        default  never
platform  platform  git.example.com  D:\platform      own      never
```

With `--json` it prints a single-line JSON array with everything a script needs
to reason about a run; `lastSync` is the UTC timestamp of the last completed
sync and `lastSyncSummary` its summary line, both `null` until the first sync;
`tokenSource` is `own` or `default`; `forge` is `github` or `gitea` (`unknown`
for an account a later version of gclo added on a forge this one does not know),
and `server` is the address of a Gitea account's server as the token is sent to
it (`null` for GitHub, and for an account whose stored address cannot be used).
The last two fields are new in 1.2:

```json
[{"id":"d0f0e0c09c404a5e8f3a5b1e6f7a2c11","name":"work","description":"","organization":"contoso","targetRoot":"C:\\src\\contoso","createOrgSubfolder":true,"maxConcurrency":8,"lastSync":"2026-07-04T14:12:03+00:00","lastSyncSummary":"Finished: 3 cloned, 41 updated, 0 failed, 0 canceled of 44.","tokenSource":"own"}]
```

`id` is the key of the account's Credential Manager entry (`gclo:account:<id>`)
for an `own` account on GitHub; a `default` account has no entry of its own and resolves
to the default token saved in the desktop app's Settings whenever it syncs.
When no accounts exist yet, stdout stays empty (`--json` prints `[]`) and a
hint goes to stderr; the exit code is still 0.

### `gclo accounts add`

```powershell
gh auth token | gclo accounts add --name work --org contoso --target C:\src\contoso --org-subfolder --parallel 16 --token-stdin
```

`--name`, `--org`, and `--target` are required; `--parallel` (1-64, default 8),
`--org-subfolder`, and `--description` are optional. A relative `--target` is
stored as a full path, resolved against the current directory, because the
account is used later from other places; a target that cannot be a folder
(a character no folder name can have, a device name, an unexpanded `~` or
`%NAME%`) is refused. `--description=` (or `--description ""`) is an empty
description. The token comes from a
token option exactly as for `gclo sync` (default: the `GITHUB_TOKEN` environment
variable, or `GITEA_TOKEN` for a Gitea server) and is written to Windows
Credential Manager — never put it on the command line. A name that another
account already uses (case-insensitive) exits with code 2.

For an account on a Gitea server, add `--server` (see "GitHub, or a Gitea
server"):

```powershell
Get-Secret -Name GiteaToken -AsPlainText | gclo accounts add --name platform --org platform --target D:\platform --server https://git.example.com --token-stdin
```

The forge and the server are **fixed once the account is added**. The token
was saved for that place, and editing an address must not send it somewhere
else: to move an account, remove it and add it again.

With `--use-default-token` instead of a token option, the account holds no
token of its own: it resolves to the default token saved in the desktop app's
Settings every time it syncs, so replacing that token in Settings updates every
such account at once. The command fails (exit 2) when no default token is saved,
because the account could never sync; the CLI cannot set the default token.
The default token is a GitHub token: `--use-default-token` is for GitHub
accounts only, and an account on a Gitea server always has a token of its own.

```powershell
gclo accounts add --name personal --org octocat --target D:\mirror --use-default-token
```

### `gclo accounts edit`

```powershell
gclo accounts edit --name work --parallel 24 --no-org-subfolder
gh auth token | gclo accounts edit --name work --token-stdin     # rotate the stored token
gclo accounts edit --name work --rename "work (contoso)"
```

`--name` picks the account; every other option replaces just that setting.
Without a token option the token (and its source) is left untouched. A
`--token-*` option gives the account a token of its own (also switching a
`default` account to `own`); `--use-default-token` switches it to the default
token and removes its own entry from Credential Manager. Editing with nothing
to change is a usage error. `--forge` and `--server` are refused: where an
account lives cannot be changed (remove it and add it again).

### `gclo accounts remove`

```powershell
gclo accounts remove --name work
```

Deletes the account's settings and its Credential Manager entry. Repositories
on disk are not touched.

### `gclo sync --account <name>`

Runs a sync with the account's settings and its stored token, from the forge
and server the account was added for — no `--org`, `--target`, or token option
needed (`--forge` and `--server` are not accepted with `--account`):

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

gclo needs a token for both the API and the git transport: a GitHub Personal
Access Token, or an access token of the Gitea server.

> **Why is there no `--token <value>` option?**
> Command-line arguments are visible to every other process on the machine —
> Task Manager, `ps`, `wmic process`, `/proc/<pid>/cmdline` — and often end up in
> shell history and logs. A token passed as a plain argument would leak to every
> local user and program. gclo therefore only accepts tokens through channels
> that stay off the command line.

| Option | Behavior |
| --- | --- |
| *(none)* | Reads the `GITHUB_TOKEN` environment variable; for a Gitea server (`--server`) the `GITEA_TOKEN` variable instead. Neither is used for the other forge. |
| `--token-env VAR` | Reads environment variable `VAR`. |
| `--token-file PATH` | Reads the first non-blank line of `PATH`, trimmed. |
| `--token-stdin` | Reads one line from standard input — made for piping from a secret store. |

The options are mutually exclusive. A missing or empty token prints an error to
stderr and exits with code 2.

With `gclo sync --account`, the default source is the account's token in
Windows Credential Manager instead of an environment variable; any token option
above still wins for that run (the stored token is left untouched), and is sent
to the place the account lives.

## Activity log

Every invocation appends to a daily activity log file, `gclo-yyyy-MM-dd.log`,
under `%LOCALAPPDATA%\KofTwentyTwo\gclo\logs` (on Linux/macOS: the platform's
local application data folder), or under `logs` in the folder `GCLO_DATA_DIR`
names when that variable is set, the same folder the desktop app logs to. It records the run's parameters, per-repository
failures, and path-sanitization notes — the same log the desktop app shows
under **View > Activity log**. Tokens are never written to the log, or anywhere
else on disk; see [SECURITY.md](../SECURITY.md).

Daily files are kept for 30 days, today included; older ones are removed the
first time something is logged on a given day. The desktop app and the CLI
log to the same file (the stable desktop app, that is; a development build
of the app keeps its own data folder); an entry that finds the file in use is
tried again for a moment rather than dropped, and if the file stays in use,
entries are dropped until it is free again.

Each entry is one line that starts with its timestamp: local time, followed by
its offset from UTC (`2026-11-10 14:03:22.123 -06:00 [INFO] ...`). Text that came from a repository, such as a file path, is written with control characters shown as `\uXXXX`, and the lines of an exception are indented under their entry, so nothing in a repository can add or imitate an entry. Everything the CLI prints as text shows such characters the same way, and on a console whose code page cannot represent a character (code page 437 or 850, for example) that character is shown in the same form too; the JSON modes use JSON's own escaping.

## Exit codes

| Code | Meaning | What to do |
| --- | --- | --- |
| 0 | Everything succeeded (or `--dry-run` / a listing completed). | — |
| 1 | The run completed, but some repositories failed or the run was canceled (Ctrl+C). `--json`'s `wasCanceled` tells the two apart. | Inspect the failures; retry them. |
| 2 | Fatal: bad arguments, missing/empty token, organization not found, unknown account, missing account token, unusable target folder, or an account command on a non-Windows OS. | Fix the invocation. |
| 3 | The forge (GitHub, or the Gitea server) rejected the token (401), or refused it access (403: the token may not see the organization, the organization requires SAML single sign-on and the token is not authorized for it, or a Gitea token lacks a scope). The message carries the server's reason when it gave one. | Fix the token or its access; do not retry as is. |
| 4 | Not your doing: a rate limit (GitHub's primary or secondary one, 403 or 429; a Gitea server's 429), a temporary throttle, a network failure (DNS, connection, TLS, proxy), a request that timed out, an error on the server's side (HTTP 5xx), or the accounts file being changed by another gclo process at that moment. | Retry later (for a rate limit, the message says when). |
| 70 | An unexpected error inside gclo. The exception type is printed and the full details are in the activity log. | Please report it. |

Codes 3 and 4 were part of code 2 before version 1.1; a script that tested
`-eq 2` for "usage error" should treat `3` and `4` as distinct outcomes.
Before version 1.1.1, a refusal of access other than an invisible organization
(403), a rate limit answered with 429, a network failure, a timeout, and an
error on GitHub's side ended with code 70; they now end with `3` (the refusal)
or `4` (the rest).

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
  4 { Write-Warning "rate limit, network or GitHub-side failure; will retry next run" }
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
  4) echo "rate limit, network or GitHub-side failure; retrying next run" ;;
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
1.1.1 (a893741d2)
```

Released builds print the semantic version and the short commit hash; local builds print the next-release version with a `-dev` suffix (e.g. `1.1.2-dev (<hash>)`).
