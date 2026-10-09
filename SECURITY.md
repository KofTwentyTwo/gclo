# Security Policy

## Reporting a vulnerability

**Do not open a public issue for a security problem.**

Report it privately through GitHub's
[private vulnerability reporting](https://github.com/KofTwentyTwo/gclo/security/advisories/new)
(the repository's **Security** tab, then **Report a vulnerability**). That is the only
reporting channel; there is no security email address.

Security contact: **James Maes** ([@KofTwentyTwo](https://github.com/KofTwentyTwo)),
maintainer.

Please include the affected version (**Help → About gclo…** in the app, or
`gclo --version`), how to reproduce it, and the impact you see. A minimal
reproduction is enormously helpful.

## What to expect

gclo follows coordinated vulnerability disclosure. Timeframes are counted from the
day the report arrives and come from the
[KofTwentyTwo security program](https://github.com/KofTwentyTwo/standards/blob/main/policies/security-program.md)
(K22-SEC-40, K22-SEC-41):

| Step | Target |
| --- | --- |
| Acknowledge the report | 7 days |
| Assess it: confirm or reject, with a severity | 14 days |
| Release a fix for a CRITICAL vulnerability | 7 days |
| Release a fix for a HIGH vulnerability | 30 days |
| Release a fix for a MEDIUM vulnerability | 90 days |
| Release a fix for a LOW vulnerability | The next release |
| Publish the advisory (GitHub Security Advisory, CVE where applicable) | When the fix ships, or 90 days after the report, whichever comes first |

A vulnerability that is being actively exploited is announced through a security
advisory within 72 hours of the maintainer becoming aware, with any available
mitigation, even before a fix exists.

Reporters are credited in the advisory unless they ask not to be.

## Supported versions

| Version | Security fixes |
| --- | --- |
| 1.0.x (the latest release) | Yes |
| Older | No; upgrade to the latest release |

Only the latest MINOR of the latest MAJOR receives fixes; a line stops receiving
fixes when the next MINOR is released. When a new MAJOR is released, the previous
MAJOR's last MINOR receives security fixes for 6 months. There are no maintenance
branches beyond that.

Installed desktop builds update in place via **Help → Check for updates**; the CLI is
updated by downloading the latest `gclo-cli-win-x64-<version>.zip` from the
[releases page](https://github.com/KofTwentyTwo/gclo/releases) or through Scoop.

## Published vulnerabilities

Fixed vulnerabilities are published as
[GitHub Security Advisories](https://github.com/KofTwentyTwo/gclo/security/advisories)
with a CVE identifier, affected and fixed versions, and credit to the reporter.

## How gclo handles your GitHub token

gclo works with GitHub Personal Access Tokens, so token safety is part of the design.
The full picture is in the [threat model](docs/security/threat-model.md).

- **Quick Sync tokens are kept in memory only.** A token pasted into Quick Sync is used
  for the GitHub API and as the git HTTPS credential for the duration of the session,
  and is never written to disk, settings, or logs.
- **Saved-account tokens and the default token are persisted in the Windows Credential
  Manager, nowhere else.** When you save an account (in the app or with
  `gclo accounts add`), or set a default token in Settings, the token is stored as a
  generic credential in the per-user Windows Credential Manager (DPAPI-protected,
  non-roaming) under a `gclo:account:<id>` target (`gclo:<scope>:account:<id>` when
  `GCLO_DATA_DIR` points the app at a non-default data directory, so an isolated data
  directory never sees the default profile's tokens), and removed when the account or
  token is deleted. It is **never** written to `settings.json`, `accounts.json`, or
  any log; those files hold only non-secret metadata. This is why accounts are
  Windows-only (K22-SEC-71).
- **The trust boundary of stored tokens is the Windows user account.** Credential
  Manager protects a stored token from other users and from offline access, not from
  code running as the same user: any process you run can read it with `CredRead`,
  exactly as it can read your git credentials or browser cookies.
- **The app never redisplays a stored token.** Editing an account shows an empty token
  box with a placeholder; the stored value is read only when it must be sent to GitHub.
  Activity-log entries about tokens record only that one was entered or replaced, and
  its length.
- **The CLI refuses tokens on the command line.** There is deliberately no
  `--token <value>` option; tokens are accepted only via environment variable
  (`--token-env`, default `GITHUB_TOKEN`), a file (`--token-file`), or standard input
  (`--token-stdin`). See [docs/CLI.md](docs/CLI.md).
- **Clone in WSL** hands the token to the distribution's git only through the
  environment (`WSLENV`) and an inline credential helper, never on a command line or
  in a file.
- **Secret-scanning push protection, gitleaks, and CodeQL** run on this repository, so
  credentials cannot be committed and token-handling regressions are caught in CI.

If you find any code path where a token can end up in a file other than Credential
Manager, in a log, in process output, on a command line, or redisplayed by the UI, that
is a vulnerability: please report it through the channel above.

## How releases are trusted

- Releases are built only by the `Release` workflow from a tag that must be contained
  in `main`, after the full CI gates. Every published asset is listed in `SHA256SUMS`,
  carries a [build-provenance attestation](https://docs.github.com/en/actions/security-for-github-actions/using-artifact-attestations),
  and ships with a CycloneDX SBOM. See "Verifying a release" in the
  [README](README.md#verifying-a-release).
- Self-update downloads only from this repository's GitHub Releases and verifies each
  package against the release's `releases.<channel>.json` feed.
- The binaries are **not yet Authenticode-signed**: KofTwentyTwo standards exception
  [EX-0002](https://github.com/KofTwentyTwo/standards/blob/main/exceptions/register.md#ex-0002)
  (tracked here as [#57](https://github.com/KofTwentyTwo/gclo/issues/57)). Until they
  are, expect a SmartScreen prompt on first install, and prefer the checksum and
  attestation checks over trusting a download by its name.
