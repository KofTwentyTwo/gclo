# Contributing to gclo

Thanks for your interest in contributing. This document covers what you need to get set up and what is expected of a pull request.

## Prerequisites

- Windows 11, or Windows 10 version 1809 (build 17763) or later
- [.NET 10 SDK](https://dotnet.microsoft.com/)
- Visual Studio 2026 with the Windows App SDK / WinUI workload (recommended for working on the app UI; the engine and its tests only need the SDK)

## Building and testing

```powershell
# Packaged build (what F5 in Visual Studio does)
dotnet build gclo.slnx -p:Platform=x64

# Unpackaged build — verify this still works before opening a PR
dotnet build gclo.slnx -p:Platform=x64 -p:WindowsPackageType=None

# Run the test suite
dotnet test
```

## Pull request expectations

- **Zero warnings.** The build must complete with 0 warnings. Treat any new warning as a failure.
- **Tests pass.** `dotnet test` must be green. Engine changes should come with corresponding xunit tests in `gclo.Engine.Tests`.
- **Unpackaged mode keeps working.** The app must run when built with `-p:WindowsPackageType=None`. Do not call APIs that require package identity (for example `Windows.Storage.ApplicationData` or `Package.Current`) without a try/catch fallback.
- Keep changes focused; unrelated refactors belong in separate PRs.

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

- Engine code stays UI-free: `gclo.Engine` is a plain class library behind `IRepositoryLister` / `IGitClient` interfaces so it remains testable without the app.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
