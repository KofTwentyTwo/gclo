# Threat model: gclo

<!-- K22-SDLC-03. Update in any pull request that changes the attack surface, and
     review it in every MINOR/MAJOR release checklist (K22-SEC-30).
     Method: data-flow diagram + STRIDE per trust boundary. -->

- **Version reviewed:** v1.0.1 · **Last reviewed:** 2026-10-09

## 1. What the product is and does

gclo (Git Clone Large Organizations) is a Windows 11 desktop app (WinUI 3) and a
command-line tool that mirror every repository of a GitHub organization or user
account into a local folder: it lists repositories through the GitHub REST API, then
clones the missing ones and fast-forwards the existing ones over HTTPS with a
personal access token (PAT) the user supplies. It runs as the user, with the user's
privileges, on the user's machine, and has access to that PAT, the target folder, and
(for the WSL recovery) the default WSL distribution. It sends nothing anywhere except
GitHub.

## 2. Actors and components

| Actor or component | Description | Trust level |
| --- | --- | --- |
| User | Runs the app or CLI with their own PAT and chooses the target folder | Trusted for their own data |
| gclo app / CLI | `gclo.exe` (WinUI 3) and the CLI `gclo.exe`, same engine | Trusted; runs as the user |
| GitHub REST API | Lists organizations and repositories | Untrusted input (responses are parsed), authenticated by the PAT |
| Git remotes (`github.com`) | Clone and fetch over HTTPS via libgit2 | Untrusted input (repository content, including file paths) |
| Windows Credential Manager | Stores saved-account and default tokens | Trusted store; boundary is the Windows user account |
| Local data directory | `%LOCALAPPDATA%\KofTwentyTwo\gclo` (or `GCLO_DATA_DIR`): settings, accounts, logs. Deliberately outside the Velopack install root `%LOCALAPPDATA%\gclo`, which Setup.exe deletes on reinstall (#91) | Trusted; same user |
| Target folder | Where repositories are written; may already contain repositories | Partly untrusted (pre-existing content, `.git` config) |
| WSL default distribution | Runs `git clone` for the "Clone in WSL" recovery | Semi-trusted (user-installed Linux environment) |
| Update feed | The releases of this project on `git.kof22.com`, `releases.<channel>.json` | Untrusted transport, trusted publisher: HTTPS, and package hashes taken from the same feed (see T1) |
| Package managers | Scoop bucket (this repo), Chocolatey, winget | Untrusted transport; assets verified by hash |

## 3. Data flow and trust boundaries

```mermaid
flowchart LR
    user([User]) -->|PAT, org, target| app[gclo app / CLI]
    app -- HTTPS + PAT --> api[(GitHub REST API)]
    app -- HTTPS + PAT --> git[(Git remotes)]
    app -->|tokens| cred[(Credential Manager)]
    app -->|settings, accounts, logs| data[(Data directory)]
    app -->|working trees| target[(Target folder)]
    app -->|env + script over stdin| wsl[WSL distribution]
    app -- HTTPS --> feed[(git.kof22.com releases: update feed)]
    subgraph machine [Trust boundary: user's machine]
        app
        cred
        data
        target
        wsl
    end
```

## 4. External interfaces

| Interface | Direction | Data | Authenticated? | Validated where? |
| --- | --- | --- | --- | --- |
| GitHub REST API (Octokit) | out/in | Organization and repository metadata | PAT (bearer) | `GitHubRepositoryLister` / `GitHubOrganizationLister`: typed responses; names deduplicated; `RepositoryPathResolver` contains every target path under the target root |
| Git HTTPS transport (libgit2) | out/in | Repository objects and trees | PAT as HTTPS basic (`x-access-token`) | `WindowsPathValidator` checks every incoming path against Windows rules before checkout; non-fast-forward is reported, never merged |
| PAT entry | in | The token | n/a | App: in-memory or Credential Manager; CLI: env var, file, or stdin only (no `--token <value>`) |
| Credential Manager | out/in | Tokens by `gclo[:<scope>]:account:<id>` target | Windows user session | `CredentialManagerVault`; scope derived from the data root |
| `settings.json`, `accounts.json` (with `accounts.json.lock`) | out/in | Non-secret settings and account metadata | File ACL (user profile) | Written beside the file and put in place in one step; sanitized on load; an accounts file whose content is not valid is moved aside (`.corrupt-*`), one that cannot be read is left in place; the previous accounts file is kept as `.bak`. Changes to the accounts are serialized across gclo processes by the lock file: a process of the same user that holds it open can delay account changes (they fail after a few seconds, nothing is written), not read or alter them |
| Activity logs | out | Actions and errors; token *lengths* only | File ACL | `IActivityLog` callers never pass tokens |
| `.git/gclo-recovery.json`, `gclo.checkoutpending` and `gclo.recoverymanaged` config keys | out/in | Path-recovery plan; checkout-pending marker; "gclo wrote this working tree" marker | File ACL | Written atomically; read only for the repository it belongs to (the markers from the repository's own configuration, never the user's or the machine's). A repository marked as written by gclo has its working tree rewritten on sync; one that is not, and was checked out by git, is never rewritten |
| Command line (CLI) | in | Options, org, target, filters | n/a | `OptionReader` / `RepoFilterSpec`; parallelism bounded 1..64; token options refused on argv |
| `GCLO_DATA_DIR`, `GCLO_UITEST_FIXTURE` env | in | Data root override; offline test seam | n/a | Fixture only answers for the exact fixture token (every other token, and the question whether a folder is a repository, go to the real services); ignored by an installed build; logged when enabled; never set in production (#145) |
| `wsl.exe -e sh -s` | out/in | POSIX script on stdin, org/repo/url as positional args, `GCLO_GIT_TOKEN` via `WSLENV` | PAT via env | Nothing is re-parsed by wsl.exe's command line; token never on argv or disk; UTF-16 wsl.exe messages decoded |
| Update feed (Velopack) | in | `releases.<channel>.json`, packages | HTTPS; package hashes | Velopack verifies package hashes against the feed; channel never crosses |
| Release assets (users) | in | Setup, portable, CLI zip, nupkg, SBOMs | HTTPS | `SHA256SUMS`, build-provenance and SBOM attestations (`gh attestation verify`) |

## 5. Assets

- The user's GitHub PAT (read access to private repositories, possibly more).
- The contents of the user's repositories on disk and the integrity of their working
  trees (a fast-forward-only policy means gclo never manufactures commits).
- The ability to run code on the user's machine (a malicious update or installer).
- The ability to ship a malicious release to every installed app (release pipeline).
- The user's saved account metadata (organization names, folder paths).

## 6. Threats (STRIDE)

| # | Boundary / component | Category | Threat | Likelihood | Impact | Mitigation | Status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| T1 | Update feed | Tampering | Attacker serves a malicious update | Low | Critical | HTTPS to the project's releases on `git.kof22.com` only (ADR 0009); Velopack verifies each package against the size and hash in the feed. The feed and the packages come from the same server, and the updater does not check an Authenticode signature or an attestation: whoever controls that server, the repository's release permission, or the server's TLS certificate can deliver a package that installs without a warning. The mitigations are therefore the server's own: who may publish a release there, how its certificate and DNS are held. Releases are not immutable on Gitea. | Accepted for 1.1.1; depends on the server's access control |
| T2 | Package managers / downloads | Tampering | Modified installer or CLI zip on a mirror | Low | High | `SHA256SUMS`; Scoop and Chocolatey manifests pin the hash; "Verifying a release" instructions | Mitigated |
| T3 | Git remotes | Tampering / EoP | Repository with Windows-invalid or traversal paths (`..`, reserved names, case collisions) writes outside or corrupts the target | Medium | High | Pre-checkout validation of every incoming path, which also refuses a `.git` segment in any spelling Windows opens as that folder; `RepositoryPathResolver` containment; recovery by rename/skip or clone in WSL; `core.longpaths`. The recovery materializes the tree itself, so every write and delete it makes goes through `WorkingTree`: inside the working directory, never in `.git`, never through a link, and an existing file is replaced rather than written through (#141). Residual: the link check and the write are two steps, so a link swapped in between them by something already running as the user is not caught | Mitigated |
| T4 | Git remotes | Tampering | Hostile `.git` config or hooks in a pre-existing target repository | Low | High | gclo runs no hooks (libgit2, no shell); only fetch and fast-forward; `gclo.checkoutpending` marks incomplete trees; a folder is fetched only when its `origin` names the listed repository, and the remote contacted is the listed URL, not origin's spelling of it; a `url.<base>.insteadOf` rewrite in the folder's own configuration is refused, and so is its `core.sshCommand` when the fetch is not HTTPS (`RemoteUrl`, #139). The user's global git configuration still applies, as it does to git itself. The recovery file `.git\gclo-recovery.json` is validated when it is loaded: a listed path that is not in the working tree fails the row instead of being deleted (#141) | Mitigated for the remote, the transport program, and the recovery file; other `.git` content of an existing folder is still trusted |
| T5 | PAT handling | Information disclosure | Token leaks to disk, logs, process listings, or UI | Medium | Critical | Memory-only in Quick Sync; Credential Manager otherwise; accounts may reference the default token (`TokenSource`), resolved at use time through one resolver and never copied; logs record lengths; no `--token`; WSL via env only; secret scanning + gitleaks + CodeQL in CI | Mitigated |
| T6 | Credential Manager | Information disclosure | Another process as the same user reads the stored token | Medium | High | Documented boundary (SECURITY.md); vault scoped per data directory so test or portable instances never see the default profile's tokens | Accepted (OS boundary) |
| T7 | GitHub API | Denial of service | Rate limits or abuse detection stop a sync | Medium | Low | Secondary-rate-limit and abuse handling with backoff; per-repo failure isolation; cancellation | Mitigated |
| T8 | GitHub API / remotes | Spoofing | A MITM impersonates GitHub | Low | Critical | HTTPS with system trust store (Octokit, libgit2); no HTTP fallback: an existing folder whose origin is `http://` or SSH is fetched through the listed HTTPS URL instead (unless the user's own global configuration rewrites that URL) | Mitigated |
| T9 | WSL recovery | Elevation of privilege | Hostile org/repo names injected into the shell script, or used to leave `~/gclo` | Low | High | Script fed on stdin with positional parameters; organization and repository each validated as one plain name inside `WslCloner` (and the organization again before any listing request); the URL must be HTTPS and is passed after `--`; no string interpolation into the script | Mitigated |
| T10 | Logs and crash output | Information disclosure | Exception text carries a token or private path | Low | Medium | Crash net logs messages only; log review in incident handling; tokens never in exception messages (tests) | Mitigated |
| T11 | Release pipeline | Tampering | Compromised action or cache poisons a release | Low | Critical | Build runs in the standards' shared release workflow (SLSA Build L3 provenance, `--signer-repo KofTwentyTwo/standards`), SHA-pinned actions, no caches, read-only tokens, publishing tokens only in the tag-restricted `release` environment, `protect-release-tags`, required gates re-run on the tag | Mitigated |
| T12 | Repository | Repudiation | Unattributed change reaches `main` | Low | High | Signed, signed-off commits; squash-only through gated pull requests; no bypass actors | Mitigated |
| T14 | PAT handling | Information disclosure | The token is offered to a host other than the one the repository was listed on: an existing folder whose `origin` points elsewhere, or a server named by the repository's own configuration during the WSL clone (for example an LFS endpoint) | Low | Critical | The git credential callback answers only a remote whose URL is HTTPS on the listed URL's host and port, and refuses every other remote with a clear error (a redirect that the listed host itself issues is followed by libgit2; that host already holds the token); a folder whose origin names another repository fails before any request; the WSL credential helper is scoped to the listed host (`credential.https://<host>.helper`) and an existing WSL destination is pulled only when its stored origin is the listed URL (#139) | Mitigated |
| T15 | Logs and terminal | Spoofing / Tampering | Text from a repository or a server (a file path may contain a newline, an escape sequence, or a character that reorders or hides text) forges an entry in the activity log or drives the terminal the CLI prints to | Low | Medium | `DisplayText` shows control, separator and format characters (direction marks and overrides, invisible characters, tag characters) and unpaired surrogates as visible `\uXXXX` text: every log message is one line, and an exception's lines are indented under their entry so none can start like one. The CLI writes every line through one place, `Terminal`, which also shows any character the console's code page cannot represent in that form, because a legacy code page turns some printable characters into control bytes; a test fails if any other CLI file writes to the console. The JSON modes rely on the JSON encoder (#143) | Mitigated for the log and the CLI. Not covered: the desktop app's own surfaces (row error text, the path-recovery dialog), where a newline is harmless but a direction override in a path the user is asked to approve is not; and text that merely looks like something else (look-alike letters, an escape written out literally) |
| T13 | CLI / data directory | Tampering | A malicious `accounts.json` or `settings.json` redirects targets | Low | Medium | Files live under the user profile; sanitized on load; target paths resolved and contained | Accepted (same-user boundary) |

## 7. Accepted risks

- T6 and T13: code running as the same Windows user can read Credential Manager
  entries and edit the data directory; that is the platform's boundary and is stated
  in SECURITY.md.
- Windows binaries are not yet Authenticode-signed: standards exception EX-0002 (#57).
  Compensated by immutable releases, `SHA256SUMS`, and attestations.

## 8. Review log

| Date | Version | Reviewer | Changes |
| --- | --- | --- | --- |
| 2026-10-09 | v1.0.1 | James Maes (@KofTwentyTwo) | Initial model, covering the path-recovery, WSL clone, vault scoping, package-manager, and SBOM changes of 1.0.1 |
| 2026-10-09 | v1.0.1 | James Maes (@KofTwentyTwo) | T11 updated: releases build in the shared standards workflow; publishing tokens moved to the `release` environment (#76) |
| 2026-10-10 | v1.0.2 | James Maes (@KofTwentyTwo) | T11: the packaging PR is opened with a short-lived release GitHub App token (contents + pull requests on this repository, no bypass) instead of a long-lived PAT; the commit is GitHub-signed (#85) |
| 2026-10-10 | v1.0.2 | James Maes (@KofTwentyTwo) | Local data directory moved to `%LOCALAPPDATA%\KofTwentyTwo\gclo`: the Velopack install root is deleted by Setup.exe, which destroyed accounts.json and settings.json (#91) |
| 2026-10-10 | v1.0.2 | James Maes (@KofTwentyTwo) | T2/T11: releases are Authenticode-signed with Azure Artifact Signing over GitHub OIDC (no signing secret; federated credential bound to this repository's `release` environment; signer role scoped to the certificate profile) and the published assets are verified with `signtool verify /pa /all` (#57, ADR 0008) |
| 2026-10-10 | v1.1.0 | James Maes (@KofTwentyTwo) | T5: accounts can reference the default token (`TokenSource = Default`) instead of holding a copy; one `AccountTokenResolver` serves the app and the CLI, so rotating the default token in Settings rotates every such account (#102, docs/plans/default-token-ux.md) |
| 2026-11-10 | v1.1.1 | James Maes (@KofTwentyTwo) | T14 added, T4/T8/T9 updated: an existing folder is fetched only when its origin names the listed repository, through the listed URL, and the folder's own `url.*.insteadOf` and (for a non-HTTPS fetch) `core.sshCommand` are refused; the git credential callback and the WSL credential helper answer only for the listed host over HTTPS; WSL organization and repository names validated as single names (#139) |
| 2026-11-10 | v1.1.1 | James Maes (@KofTwentyTwo) | T3/T4 updated: path recovery writes and deletes only through `WorkingTree` (inside the working directory, never in `.git`, never through a link); `.git` and its Windows aliases are invalid path segments; the stored recovery file's paths are validated on load; cleanup of a failed clone does not follow links (#141) |
| 2026-11-10 | v1.1.1 | James Maes (@KofTwentyTwo) | T15 added: text from repositories and servers is shown with control, separator and format characters made visible, in the activity log and in everything the CLI prints as text, including characters a legacy console code page would turn into control bytes; the desktop app's surfaces are named as not covered (#143) |
| 2026-11-10 | v1.1.1 | James Maes (@KofTwentyTwo) | The project's home moved to `git.kof22.com`: the Help menu, the About dialog, the CLI's error text and the documentation link there, and the Scoop bucket is added from there (the manifest that pins the download URL and its hash now comes from that server over HTTPS; T2 is unchanged in kind). Help > Share hands a fixed text (what gclo is, where to download it, the sender's gclo version) to the system's mail handler as a `mailto:` link with no recipient, or shows it and copies it to the clipboard; nothing about the user, their accounts, or their repositories is in it, and the address it is built from must be a plain http(s) host and path (#163, #165). The update feed and the release downloads named in package manifests are still GitHub Releases at this revision. |
| 2026-11-10 | v1.1.1 | James Maes (@KofTwentyTwo) | Working tree (#174; the T3 and T4 rows are unchanged, this adds to what they rely on). On Windows, a forced checkout into a folder whose tree is not finished (the first checkout of a clone that was empty, and the completion of a checkout that was cut off) is preceded by refusals that change nothing: a link on the way or at a destination (except, at a destination where the repository itself has a symbolic link, a link that leads where the repository's does), a file where a folder is needed (also on the way to a submodule), a folder that is not empty where a file is needed, a file at a destination that is not what the unfinished checkout wrote there, and anything but an empty folder where the repository has a submodule. "What the unfinished checkout wrote" is the content, or the beginning of it, of the commit the branch is on or of a commit recorded in `gclo.checkoutwrittenbefore`, as stored or as the checkout filters make it, carriage returns aside. An unfinished checkout is a marked folder that has no index, or that does not hold every file of its commit; a marked folder that has an index and every file is updated like any clone, without force. A completion is refused when the index names a file the commit does not have (somebody added it with git, and the forced checkout would remove it). An unfinished checkout is completed at the commit its branch is on, before the branch moves, except where the branch must move first (a tree Windows cannot write, or a history origin rewrote); the commit it leaves is then recorded, and a path recovery of that folder accepts a file that is, whole, what a recorded commit has. A repository that has local branches is not treated as an empty clone; one without follows origin's default branch only when its folder holds nothing but `.git`. The folder is marked unfinished before its branch is created. One git operation per folder at a time within a process (not across processes, and not across spellings of a path that only the file system can resolve). The WSL clone is made in `~/gclo/.gclo-tmp` and renamed into place when complete (`mv -T`, after a check that nothing is there). Not covered: a file created in the folder between the check and the checkout that follows it; a file of the user's at a destination that is empty, or holds the repository's content or the beginning of it, which is replaced; a link of the user's that leads exactly where the repository's symbolic link at that path does, which is replaced by the repository's; a marked folder whose index somebody's git wrote and whose last file was cut off, which is taken for finished (git shows that file as changed); files of a commit the branch left that the new commit does not have, which stay in the folder untracked; an unfinished clone in which a folder of an earlier commit became a file, or that was cut off by a power loss leaving files of zeros, which is refused until the folder is cleared; platforms other than Windows, where the checkout is forced as before. |
| 2026-11-10 | v1.1.1 | James Maes (@KofTwentyTwo) | Update feed and release downloads moved from GitHub Releases to the project's releases on `git.kof22.com` (ADR 0009, #170): the in-app updater reads the feed with Velopack's Gitea source, anonymously over HTTPS, and the Scoop and Chocolatey manifests download from there. The updater verifies packages against the hashes in the feed, as before; it has never checked a signature or an attestation. The trust that was placed in GitHub's release hosting (account security, immutable releases) is now placed in a self-hosted server: whoever can publish a release there, or holds its TLS certificate or DNS, can deliver an update that installs without a warning. T1 is rewritten to say so and its status changed from mitigated to accepted. The manifests still pin a SHA-256 (T2). |
