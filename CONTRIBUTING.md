# Contributing to gclo

Thanks for your interest in contributing. This document covers what you need to get set up, how changes flow through the repository, and what is expected of a pull request.

Before starting on a feature, check the [development roadmap (#11)](https://github.com/KofTwentyTwo/gclo/issues/11) — it may already be planned (or deliberately out of scope). For anything non-trivial, open an issue first so the approach can be agreed before you invest time.

## Prerequisites

- Windows 11, or Windows 10 version 1809 (build 17763) or later
- [.NET 10 SDK](https://dotnet.microsoft.com/)
- Visual Studio 2026 with the Windows App SDK / WinUI workload (recommended for working on the app UI; the engine, the CLI, and the tests only need the SDK)

## Branch model

- **`dev`** is the integration branch. **Pull requests target `dev`**, not `main`.
- **`main`** is the stable branch. It only moves via reviewed PRs from `dev` and is protected by required status checks. Releases are tagged from `main`.

Fork the repository, branch from `dev`, and open your PR against `dev`.

## Building and testing

All commands run from the repository root:

```powershell
# Packaged build (what F5 in Visual Studio does).
# CI runs this with -warnaserror — the build must produce ZERO warnings.
dotnet build gclo.slnx -p:Platform=x64

# Unpackaged build — runs without MSIX deployment or package identity.
# Verify this still works before opening a PR.
dotnet build gclo.slnx -p:Platform=x64 -p:WindowsPackageType=None

# Run the unit/integration test suites (engine + view models, and the CLI)
dotnet test gclo.Engine.Tests
dotnet test gclo.Cli.Tests

# Coverage as CI measures it (must be 100% for gclo.Engine, gclo.ViewModels, gclo)
dotnet test gclo.Engine.Tests --settings coverage.runsettings --collect:"XPlat Code Coverage"
dotnet test gclo.Cli.Tests --settings coverage.runsettings --collect:"XPlat Code Coverage"

# UI end-to-end tests (build the app first, then drive the real exe)
dotnet build gclo/gclo.csproj -p:Platform=x64 -p:WindowsPackageType=None
dotnet test gclo.UiTests/gclo.UiTests.csproj

# Formatting/style gate — must report no changes needed
dotnet format gclo.slnx --verify-no-changes --severity error
```

## Debugging in Visual Studio: expect (and silence) exception breaks

Per-repo failure isolation is exception-based by design: a repository that
cannot be cloned or pulled throws (`LibGit2SharpException`,
`InvalidRepositoryPathsException`, ...) and the sync engine catches it, marks
that row Failed, and keeps going. Many of these exceptions surface from inside
native libgit2 frames, so with **Just My Code** enabled Visual Studio breaks on
them as "user-unhandled" even though they are always caught — during a sync
with failing repositories the debugger pauses every thread on each one, which
looks like the whole UI is frozen until you press Continue.

None of this happens outside the debugger. Options, in order of preference:

1. Run without the debugger (**Ctrl+F5**) unless you are actively debugging.
2. In **Debug → Windows → Exception Settings**, uncheck *Break when this
   exception type is user-unhandled* for `LibGit2Sharp.LibGit2SharpException`
   (and `gclo.Engine.InvalidRepositoryPathsException`) after the first break.
3. Disable **Tools → Options → Debugging → Enable Just My Code** — the
   debugger then only breaks on genuinely unhandled exceptions.

## What CI enforces

Every pull request must pass:

| Check | What it gates |
| --- | --- |
| **Build and test (x64)** | `dotnet build gclo.slnx -p:Platform=x64 -warnaserror` — zero warnings, including NuGetAudit vulnerability warnings; then the test suites with coverage. **Line coverage must be 100%** on each of `gclo.Engine`, `gclo.ViewModels`, and `gclo` (the CLI), measured via `coverage.runsettings` (source-generated code and the `[ExcludeFromCodeCoverage]` native/network adapters are excluded — see that file) |
| **UI end-to-end tests (x64)** | FlaUI/UIA smoke tests drive the real `gclo.exe` (`gclo.UiTests`) |
| **Format (style gate)** | `dotnet format gclo.slnx --verify-no-changes --severity error` |
| **Dependency review** | New/changed dependencies must have no known vulnerabilities (any severity fails) and a license on the repo's allowlist |
| **CodeQL** | Static security analysis (runs on PRs targeting `main` and weekly) |

Running the build, test, and format commands above locally before pushing will catch almost everything CI would.

## Pull request expectations

- **Zero warnings.** The build must complete with 0 warnings. Treat any new warning as a failure.
- **Tests pass.** `dotnet test gclo.Engine.Tests` must be green. Engine changes should come with corresponding xunit tests.
- **Tests stay offline.** The engine tests exercise real git operations against **local fixture repositories** created on disk (via LibGit2Sharp) — no network access, no GitHub calls, no tokens. Keep new tests that way; fake `IRepositoryLister` / `IGitClient` implementations live in `gclo.Engine.Tests/Fakes.cs`.
- **Unpackaged mode keeps working.** The app must run when built with `-p:WindowsPackageType=None`. Do not call APIs that require package identity (for example `Windows.Storage.ApplicationData` or `Package.Current`) without a try/catch fallback.
- **Docs follow behavior.** If your change alters user-visible behavior, update the affected docs (`README.md`, `docs/CLI.md`) in the same PR.
- Keep changes focused; unrelated refactors belong in separate PRs.

## Architecture rule: no business logic in the UI project

The `gclo` WinUI project contains XAML views, dialogs, and self-update plumbing — **nothing else**. View models and settings persistence live in `gclo.ViewModels`, a UI-framework-free library the test suite exercises headlessly. All sync/GitHub/git logic lives in `gclo.Engine`, a plain class library behind the `IRepositoryLister` / `IGitClient` / `IOrganizationLister` interfaces, shared by the scriptable CLI head (`gclo.Cli`). If you find yourself writing logic in the `gclo` project, it belongs in a library — GitHub or git logic in the engine, presentation state in the view models.

## Code style

Match the existing code. In particular:

- `ImplicitUsings` is **off** in the app project — every `.cs` file needs explicit `using` directives.
- Nullable reference types are enabled; keep code null-clean rather than suppressing warnings.
- For CommunityToolkit.Mvvm observable properties, use the **partial property** form:

  ```csharp
  [ObservableProperty]
  public partial string Foo { get; set; }
  ```

  Do not use the field-based form (it emits MVVMTK0045 warnings).

- Never write a token to disk, logs, or process output, and never accept one as a command-line argument. See [SECURITY.md](SECURITY.md).

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
