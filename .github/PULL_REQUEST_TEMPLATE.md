<!-- PRs target the `dev` branch (see CONTRIBUTING.md). -->

## What

<!-- What does this PR change? -->

## Why

<!-- Why is the change needed? Link the issue if one exists (e.g. "Fixes #42"). -->

## Checklist

- [ ] `dotnet build gclo.slnx -p:Platform=x64 -warnaserror` succeeds with **0 warnings**
- [ ] `dotnet test gclo.Engine.Tests` passes (new engine behavior has tests)
- [ ] `dotnet format gclo.slnx --verify-no-changes --severity error` is clean
- [ ] No business logic in the `gclo` UI project — engine work lives in `gclo.Engine`
- [ ] Docs updated (`README.md`, `docs/CLI.md`) if user-visible behavior changed
