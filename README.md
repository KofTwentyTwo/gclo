# gclo — Git Clone Large Organizations

**Clone an entire GitHub organization. Keep it up to date. In parallel.**

[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![Platform: Windows 11](https://img.shields.io/badge/platform-Windows%2011-blue)](https://github.com/KofTwentyTwo/gclo)
[![.NET 10](https://img.shields.io/badge/.NET-10-blueviolet)](https://dotnet.microsoft.com/)

**gclo** (**G**it **C**lone **L**arge **O**rganizations) is a Windows 11 desktop app (WinUI 3, Windows App SDK) that mirrors a whole GitHub organization to a local folder in one pass. Point it at an org, give it a personal access token and a target folder, and it clones the repositories that are missing and fast-forwards the ones that already exist — with live per-repo progress and bounded parallelism.

<!-- TODO: screenshot — ![gclo main window](docs/screenshot.png) -->

## Features

- Syncs an entire GitHub organization in one pass: clones new repositories, fast-forwards existing ones
- Parallel git operations with bounded concurrency (default 8)
- Live per-repo status: Queued, Cloning (with transfer percentage), Pulling, Done, Failed, Canceled
- Overall progress bar and end-of-run summary (total / cloned / updated / failed / canceled)
- Cancellation: in-flight repos stop cleanly, unstarted repos are marked Canceled
- Per-repo failure isolation — one broken repo never aborts the rest
- Failed clones are cleaned up so a partial checkout is never mistaken for a valid repo on the next run
- Testable core: the sync engine is a plain .NET class library behind `IRepositoryLister` / `IGitClient` interfaces, covered by xunit tests

## Getting started

### Requirements

- Windows 11, or Windows 10 version 1809 (build 17763) or later
- [.NET 10 SDK](https://dotnet.microsoft.com/)
- Visual Studio 2026 with the Windows App SDK / WinUI workload (for F5 debugging of the packaged app)
- A GitHub personal access token (see [Usage](#usage) for the required scopes)

### Run from Visual Studio

1. Clone the repository:

   ```powershell
   git clone https://github.com/KofTwentyTwo/gclo.git
   ```

2. Open `gclo.slnx`.
3. Set `gclo` as the startup project and select the `x64` platform.
4. Press F5 (packaged deployment).

### Build from the command line

```powershell
# Packaged build
dotnet build gclo.slnx -p:Platform=x64

# Unpackaged build — runs without MSIX deployment or package identity
dotnet build gclo.slnx -p:Platform=x64 -p:WindowsPackageType=None

# Run the engine test suite
dotnet test
```

The unpackaged build produces a plain executable under `gclo\bin\x64\Debug\` that you can launch directly — no installer or package registration required.

## Usage

1. **Paste your token first.** The organization dropdown fills with the orgs the token can see; if the token cannot list your orgs, type the org name manually.
2. **Pick the organization** from the dropdown.
3. **Choose a target folder.** Each repository syncs into a subfolder named after the repo.
4. **Press Sync.** Watch per-repo progress live; cancel at any time.

### Token scopes

| Token type | What you need |
| --- | --- |
| Classic PAT | `repo` scope (read access to the org's repositories); optionally `read:org` so the organization dropdown can list your orgs |
| Fine-grained PAT | Repository read access for the organization |

The token is used in memory for the duration of a sync and is never stored on disk.

## How it works

The engine first lists every repository in the organization through the GitHub REST API using Octokit, following pagination to the end (100 repos per page). Each repo is reported as Queued, then `OrgSyncEngine` runs the set through `Parallel.ForEachAsync` bounded by `MaxConcurrency`. If the target folder already contains a valid git repository, it is updated; otherwise it is cloned. Every repo ends in exactly one terminal state — Done, Failed (with the error message), or Canceled — and per-repo exceptions are contained so the rest of the run continues.

Git operations are performed with LibGit2Sharp: clones report object-transfer progress, and updates do a fetch from `origin` followed by a fast-forward-only merge of the current branch onto its upstream. For both the API and git transport, the PAT is the only credential — on the git side it is sent as HTTPS basic credentials with username `x-access-token` and the token as the password.

A few behavioral notes:

- Updates are fast-forward only: if a local branch has diverged from its upstream, the repo is reported as Failed rather than auto-merged. gclo never manufactures merge commits.
- Archived repositories are included in the sync; they are read-only on GitHub but still clone and update normally.

## Project layout

| Project | Description |
| --- | --- |
| `gclo` | WinUI 3 packaged desktop app (Windows App SDK, CommunityToolkit.Mvvm) |
| `gclo.Engine` | Core sync engine: `OrgSyncEngine`, Octokit-based repository lister, LibGit2Sharp git client |
| `gclo.Engine.Tests` | xunit tests for the engine, using fake `IRepositoryLister` / `IGitClient` implementations |

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for prerequisites, build and test commands, and pull request expectations.

## License

gclo is licensed under the [MIT License](LICENSE). Copyright (c) 2026 James Maes.

gclo builds on the following open-source projects:

| Dependency | License |
| --- | --- |
| [Octokit](https://github.com/octokit/octokit.net) | MIT |
| [LibGit2Sharp](https://github.com/libgit2/libgit2sharp) | MIT |
| [libgit2](https://github.com/libgit2/libgit2) (native library used by LibGit2Sharp) | GPLv2 with linking exception |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT |
| [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) | MIT |
