# Security Policy

## Supported versions

Only the **latest release** receives security fixes — there are no maintenance branches for older versions.

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

- **Quick Sync tokens are kept in memory only.** A token pasted into Quick Sync is used for the GitHub API and as the git HTTPS credential for the duration of the session, and is never written to disk, settings, or logs.
- **Saved-account tokens and the default token are persisted — in the Windows Credential Manager, nowhere else.** When you save an account (in the app or with `gclo accounts add`), or set a default token in Settings, the token is stored as a generic credential in the per-user Windows Credential Manager (DPAPI-protected, non-roaming) under a `gclo:account:<id>` target, and removed when the account or token is deleted. It is **never** written to `settings.json`, `accounts.json`, or any log — those files hold only non-secret metadata (organization, target folder, parallelism, theme, last-sync summary). This is why accounts are Windows-only.
- **The trust boundary of stored tokens is the Windows user account.** Credential Manager protects a stored token from other users and from offline access, not from code running as the same user: any process you run can read it with `CredRead`, exactly as it can read your git credentials or browser cookies. Treat a machine where untrusted code runs as your user as a machine where the token is exposed.
- **The app never redisplays a stored token.** Editing an account or opening a connection flyout shows an empty token box with a placeholder; the stored value is read from Credential Manager only when it must be sent to GitHub (a sync, an organization lookup, a validation on save). Activity-log entries about tokens record only that one was entered or replaced, and its length.
- **The CLI refuses tokens on the command line.** There is deliberately no `--token <value>` option; tokens are accepted only via environment variable (`--token-env`, default `GITHUB_TOKEN`), a file (`--token-file`), or standard input (`--token-stdin`). See [docs/CLI.md](docs/CLI.md).
- **Secret-scanning push protection is enabled** on this repository, so credentials cannot be accidentally committed and pushed.

If you find any code path where a token can end up in a file other than Credential Manager, in a log, in process output, on a command line, or redisplayed by the UI, that is a vulnerability — please report it through the channel above.

## How releases are trusted

- Releases are built only by the `Release` workflow from a tag that must be contained in `main`, after the full CI gates, and every published asset carries a [build provenance attestation](https://docs.github.com/en/actions/security-for-github-actions/using-artifact-attestations) you can verify with `gh attestation verify <file> -R KofTwentyTwo/gclo`.
- Self-update downloads only from this repository's GitHub Releases and verifies each package against the release's `releases.<channel>.json` feed.
- The binaries are **not yet Authenticode-signed** (tracked in [#57](https://github.com/KofTwentyTwo/gclo/issues/57)); until they are, expect a SmartScreen prompt on first install, and prefer the attestation check above over trusting a download by its name.
