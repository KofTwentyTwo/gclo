# Security Policy

## Supported versions

gclo is pre-1.0 software. Only the **latest release** receives security fixes — there are no maintenance branches for older versions.

| Version | Supported |
| --- | --- |
| Latest release ([releases page](https://github.com/KofTwentyTwo/gclo/releases)) | Yes |
| Anything older | No — update to the latest release |

Installed desktop builds can update in place via **Help → Check for updates**; the CLI is updated by downloading the latest `gclo-cli-win-x64.zip` from the releases page.

## Reporting a vulnerability

**Please do not open a public issue for security problems.**

This repository has GitHub **private vulnerability reporting** enabled. To report a vulnerability:

1. Go to the repository's [Security tab](https://github.com/KofTwentyTwo/gclo/security).
2. Click **"Report a vulnerability"** (or use the direct link: [new security advisory](https://github.com/KofTwentyTwo/gclo/security/advisories/new)).
3. Describe the issue, how to reproduce it, and the impact you see. A minimal reproduction is enormously helpful.

That is the only reporting channel — there is no security email address.

### What to expect

gclo is maintained by a single person in their spare time, so response times are best-effort rather than contractual:

- You should normally get an acknowledgment within **7 days**.
- Confirmed vulnerabilities are fixed as fast as the maintainer reasonably can, and the fix ships in a new release (there is no backporting — see supported versions above).
- Please allow a fix to be released before disclosing publicly. You will be credited in the advisory unless you ask not to be.

## How gclo handles your GitHub token

gclo works with GitHub Personal Access Tokens, so token safety is part of the design:

- **The PAT is kept in memory only.** It is used for the GitHub API and as the git HTTPS credential for the duration of a run, and is never written to disk, settings, or logs. The app's persisted settings (`%LOCALAPPDATA%\gclo\settings.json`) contain only preferences such as the default folder, parallelism, and theme — never the token.
- **The CLI refuses tokens on the command line.** There is deliberately no `--token <value>` option; tokens are accepted only via environment variable (`--token-env`, default `GITHUB_TOKEN`), a file (`--token-file`), or standard input (`--token-stdin`). See [docs/CLI.md](docs/CLI.md).
- **Secret-scanning push protection is enabled** on this repository, so credentials cannot be accidentally committed and pushed.

If you find any code path where a token can end up on disk, in a log, in process output, or on a command line, that is a vulnerability — please report it through the channel above.
