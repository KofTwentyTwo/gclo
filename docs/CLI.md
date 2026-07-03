# gclo CLI

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
gclo sync --org <name> --target <folder> [--parallel N]
          [--token-env VAR | --token-file PATH | --token-stdin]
          [--json] [--quiet]
gclo orgs [--token-env VAR | --token-file PATH | --token-stdin] [--json]
gclo --version
gclo --help            (each command also accepts --help)
```

Options may be written as `--name value` or `--name=value`.

### `gclo sync`

Clones every repository of `--org` into `<target>\<repo>`. Repositories that are
already valid git repositories locally are fetched and fast-forwarded instead.
Repositories fail independently — one failure never stops the rest. Up to
`--parallel` git operations run at once (default 8).

`--org` accepts an organization login or a user account login: if the name is not
an organization, gclo falls back to the account's repositories (your own account
includes private repos the token can see).

Progress output is one line per repository status transition:

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

#### `--json`

Suppresses all progress lines and prints a single line of JSON to stdout when the
run ends (also on cancellation):

```json
{"total":45,"cloned":3,"updated":41,"failed":1,"canceled":0,"wasCanceled":false,"failures":[{"repo":"broken-repo","error":"remote authentication failed"}]}
```

### `gclo orgs`

Prints the logins the token can sync, one per line: the token's own account login
first, then its organizations alphabetically. With `--json` it prints a
single-line JSON array instead (e.g. `["octocat","contoso","fabrikam"]`).

A token that cannot list organizations (a fine-grained PAT, or a classic PAT
without the `read:org` scope) still prints its own account login — you can pass
any organization name to `gclo sync` manually.

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
| `--token-file PATH` | Reads the first non-blank content line of `PATH` (trimmed). |
| `--token-stdin` | Reads one line from standard input — made for piping from a secret store. |

The options are mutually exclusive. A missing or empty token prints an error to
stderr and exits with code 2.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Everything succeeded. |
| 1 | The run completed, but some repositories failed or the run was canceled (Ctrl+C). |
| 2 | Fatal: bad arguments, missing/empty/rejected token, or organization not found. |

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

# Machine-readable result
$result = gclo sync --org contoso --target C:\src\contoso --json | ConvertFrom-Json
if ($result.failed -gt 0) { $result.failures | ForEach-Object { "$($_.repo): $($_.error)" } }

# Nightly mirror job: quiet, check the exit code
gclo sync --org contoso --target D:\mirror\contoso --parallel 16 --quiet
if ($LASTEXITCODE -ne 0) { Write-Error "sync ended with code $LASTEXITCODE" }
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

# Machine-readable result with jq
gclo sync --org contoso --target ~/src/contoso --json |
  jq -r '.failures[] | "\(.repo): \(.error)"'

# Cron-friendly: quiet progress, failures on stderr, exit code drives alerting
gclo sync --org contoso --target /srv/mirror/contoso --parallel 16 --quiet ||
  echo "sync ended with code $?"

# List orgs as JSON and sync each one
for org in $(gclo orgs --json | jq -r '.[]'); do
  gclo sync --org "$org" --target ~/src/"$org" --quiet
done
```

## Version

```console
$ gclo --version
0.1.0
```
