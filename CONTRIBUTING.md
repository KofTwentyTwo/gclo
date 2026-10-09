# Contributing to gclo

Thanks for helping. This project follows the
[KofTwentyTwo standards](https://github.com/KofTwentyTwo/standards); this page is the
short version of what they ask of a contribution.

## Before you start

Check the [development roadmap (#11)](https://github.com/KofTwentyTwo/gclo/issues/11):
it may already be planned, or deliberately out of scope. For anything beyond a small
fix, open an issue first so the approach can be agreed before you invest time.
Questions and proposals are welcome in
[Issues](https://github.com/KofTwentyTwo/gclo/issues).

## Setup

Prerequisites:

- Windows 11, or Windows 10 version 1809 (build 17763) or later
- the .NET 10 SDK (the feature band is pinned by `global.json`)
- Visual Studio 2026 with the Windows App SDK / WinUI workload, for working on the app
  UI; the engine, the CLI, and the tests only need the SDK
- [pre-commit](https://pre-commit.com/) (or prek) for the git hooks

These are the commands CI runs; run them from the repository root before pushing:

```powershell
# Build (packaged, as F5 in Visual Studio does). Zero warnings: -warnaserror is the gate.
dotnet build gclo.slnx -p:Platform=x64 -warnaserror

# Unpackaged build: runs without MSIX deployment or package identity. Must keep working.
dotnet build gclo.slnx -p:Platform=x64 -p:WindowsPackageType=None

# Tests (engine + view models, and the CLI), with coverage as CI measures it
dotnet test gclo.Engine.Tests --settings coverage.runsettings --collect:"XPlat Code Coverage"
dotnet test gclo.Cli.Tests --settings coverage.runsettings --collect:"XPlat Code Coverage"

# UI end-to-end tests (build the app first, then drive the real exe)
dotnet build gclo/gclo.csproj -p:Platform=x64 -p:WindowsPackageType=None
dotnet test gclo.UiTests/gclo.UiTests.csproj

# Format and style gate
dotnet restore gclo.slnx --locked-mode
dotnet format gclo.slnx --verify-no-changes --severity warn --no-restore

# PowerShell scripts
Invoke-ScriptAnalyzer -Path .github/scripts -Recurse -Settings ./PSScriptAnalyzerSettings.psd1 -EnableExit

# Changed a PackageReference? Refresh the committed lock files, or the locked restore rejects it.
dotnet restore gclo.slnx --force-evaluate
```

Install the git hooks once per clone. They run the same secret, workflow, format, and
commit-message checks CI does:

```sh
pre-commit install --hook-type pre-commit --hook-type commit-msg
```

## Workflow

1. Branch from `main` with a short-lived topic branch named `<type>/<short-description>`,
   for example `feat/export-csv` or `fix/42-crash-on-empty-folder`. `main` is the only
   long-lived branch (GitHub Flow).
2. Commit with [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/)
   (`feat: ...`, `fix(sync): ...`; allowed types: `feat`, `fix`, `perf`, `refactor`,
   `docs`, `test`, `build`, `ci`, `chore`, `revert`, `style`). Every commit is
   **signed** and **signed off**: `git commit -S -s` (see *Sign-off* below).
3. Open a pull request against `main`. Its title must itself be a Conventional Commit
   header: pull requests are **squash-merged**, so the title becomes the commit
   message on `main`. Fill in the template: what, why, how it was tested.
4. Every required check must pass, and every review thread must be resolved, before
   the pull request merges. The maintainer merges; nobody pushes to `main` directly.

## What makes a contribution acceptable

- The build has **zero warnings**, and format and lint checks are clean.
- New behavior has tests; a bug fix has a test that fails without the fix.
- Line coverage stays at **100%** on `gclo.Engine`, `gclo.ViewModels`, and `gclo`
  (the CLI), measured via `coverage.runsettings`.
- Tests stay offline: engine tests use local fixture repositories and the fakes in
  `gclo.Engine.Tests/Fakes.cs`; UI tests use the offline fixture token. No network, no
  real tokens.
- Unpackaged mode keeps working: no APIs that require package identity without a
  fallback.
- User-visible changes update the docs (`README.md`, `docs/CLI.md`) in the same pull
  request; a change to the attack surface updates
  [the threat model](docs/security/threat-model.md); an expensive-to-reverse decision
  gets an ADR in `docs/adr/`.
- No secrets, credentials, or personal data, ever.
- New dependencies are justified in the pull request description.

## Checks that gate a merge

| Check | What it verifies |
| --- | --- |
| `pr / title` | The PR title is a Conventional Commit header |
| `pr / dco` | Every commit is signed off |
| `pr / dependency-review` | Added dependencies have no known vulnerabilities and an allowed license |
| `security / secrets`, `security / sca`, `security / workflows` | No secrets, no vulnerable or malicious dependencies, safe workflows |
| `codeql / analyze (csharp)`, `codeql / analyze (actions)` | No high-severity static analysis findings |
| `build-test` | Zero-warning x64 build, both unit suites, 100% coverage gate |
| `format` | `dotnet format --verify-no-changes --severity warn` and PSScriptAnalyzer |
| `ui-tests` | FlaUI end-to-end tests against the real `gclo.exe` (advisory until it is made required, #54) |

## Sign-off (Developer Certificate of Origin)

By signing off a commit you certify the
[Developer Certificate of Origin 1.1](https://developercertificate.org/): that you
wrote the change, or otherwise have the right to submit it under the project's
license. `git commit -s` appends the sign-off:

```text
Signed-off-by: Your Name <your.email@example.com>
```

The name and email must match the commit author. To add missing sign-offs to a
branch, run `git rebase --signoff main` and force-push the branch.

## Code style

The code follows the Kingsrook layout of the
[KofTwentyTwo C# profile](https://github.com/KofTwentyTwo/standards/blob/main/standards/coding/csharp-dotnet.md):
3-space indentation, Allman braces, `if(` without a space, three blank lines between
members, `///` header comments on every type and member, `var` only when the type is
apparent, one type per file. `dotnet format` applies the layout from `.editorconfig`,
and the build enforces it; Rider and ReSharper apply the blank-line rules. New
in-body comments are flower boxes; older plain `//` comments are converted as the code
around them is touched (K22-CODE-10 is a SHOULD).

In addition:

- `ImplicitUsings` is off in the app project: every `.cs` file lists its usings.
- Nullable reference types are enabled; keep code null-clean rather than suppressing.
- Observable properties use CommunityToolkit.Mvvm's partial-property form.
- No business logic in the `gclo` UI project: GitHub and git logic belongs in
  `gclo.Engine`, presentation state in `gclo.ViewModels` (see `docs/adr/0005`).
- Never write a token to disk, logs, or process output, and never accept one as a
  command-line argument. See [SECURITY.md](SECURITY.md).

## Debugging in Visual Studio: expect (and silence) exception breaks

Per-repo failure isolation is exception-based by design: a repository that cannot be
cloned or pulled throws (`LibGit2SharpException`, `InvalidRepositoryPathsException`,
...) and the sync engine catches it, marks that row Failed, and keeps going. Many of
these exceptions surface from inside native libgit2 frames, so with **Just My Code**
enabled Visual Studio breaks on them as "user-unhandled" even though they are always
caught. Run without the debugger (**Ctrl+F5**), uncheck *Break when this exception
type is user-unhandled* for those types, or disable *Just My Code*.

## AI-assisted contributions

AI coding tools are welcome. You remain the author: you must understand, test, and be
able to explain every line you submit, and you sign it off as your own. Note
substantial AI assistance in the pull request description and with a
`Co-Authored-By:` trailer naming the model.

## License

By contributing, you agree that your contributions are licensed under the project's
[MIT License](LICENSE).
