# gclo

gclo is a Windows 11 desktop tool that clones and updates every repository in a GitHub organization. Point it at an org, give it a personal access token and a target folder, and it mirrors the whole org locally — cloning repos that are missing and pulling repos that already exist — with live per-repo progress and bounded parallelism.

## Features

- Syncs an entire GitHub organization in one pass: clones new repositories, fast-forwards existing ones
- Parallel git operations with bounded concurrency (default 8)
- Live per-repo status: Queued, Cloning (with transfer percentage), Pulling, Done, Failed, Canceled
- Overall progress bar and end-of-run summary (total / cloned / updated / failed / canceled)
- Cancellation: in-flight repos stop cleanly, unstarted repos are marked Canceled
- Per-repo failure isolation — one broken repo never aborts the rest
- Failed clones are cleaned up so a partial checkout is never mistaken for a valid repo on the next run
- Testable core: the sync engine is a plain .NET class library behind `IRepositoryLister` / `IGitClient` interfaces, covered by xunit tests

<!-- TODO: screenshot — ![gclo main window](docs/screenshot.png) -->

## Requirements

- Windows 11, or Windows 10 version 1809 (build 17763) or later
- .NET 10 SDK
- Visual Studio 2026 with the Windows App SDK / WinUI workload (for F5 debugging of the packaged app)
- A GitHub personal access token with read access to the organization's repositories — either a classic PAT with the `repo` scope, or a fine-grained PAT with repository read access for the org

## Quick start

**Visual Studio**

1. Open `gclo.slnx`.
2. Set `gclo` as the startup project and select the `x64` platform.
3. Press F5 (packaged deployment).
4. Paste your PAT first — the organization dropdown fills with the orgs the token can see (or type an org name manually if the token cannot list them) — then pick a target folder and start the sync.

**Command line**

```powershell
dotnet build gclo.slnx -p:Platform=x64
dotnet test          # runs the gclo.Engine.Tests suite
```

## How it works

The engine first lists every repository in the organization through the GitHub REST API using Octokit, following pagination to the end (100 repos per page). Each repo is reported as Queued, then `OrgSyncEngine` runs the set through `Parallel.ForEachAsync` bounded by `MaxConcurrency`. If the target folder already contains a valid git repository, it is updated; otherwise it is cloned. Every repo ends in exactly one terminal state — Done, Failed (with the error message), or Canceled — and per-repo exceptions are contained so the rest of the run continues.

Git operations are performed with LibGit2Sharp: clones report object-transfer progress, and updates do a fetch from `origin` followed by a fast-forward-only merge of the current branch onto its upstream. For both the API and git transport, the PAT is the only credential — on the git side it is sent as HTTPS basic credentials with username `x-access-token` and the token as the password.

## Project layout

| Project | Description |
| --- | --- |
| `gclo` | WinUI 3 packaged desktop app (Windows App SDK, CommunityToolkit.Mvvm) |
| `gclo.Engine` | Core sync engine: `OrgSyncEngine`, Octokit-based repository lister, LibGit2Sharp git client |
| `gclo.Engine.Tests` | xunit tests for the engine, using fake `IRepositoryLister` / `IGitClient` implementations |

## Notes

- The PAT is used in memory for the duration of a sync and is never stored on disk.
- Updates are fast-forward only: if a local branch has diverged from its upstream, the repo is reported as Failed rather than auto-merged. gclo never manufactures merge commits.
- Archived repositories are included in the sync; they are read-only on GitHub but still clone and update normally.
