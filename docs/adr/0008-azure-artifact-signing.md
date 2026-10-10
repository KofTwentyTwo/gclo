# 0008. Authenticode-sign releases with Azure Artifact Signing over GitHub OIDC

- Status: accepted
- Date: 2026-10-10
- Supersedes: KofTwentyTwo standards exception EX-0002 for this repository

## Context

K22-REL-05 requires Windows executables and installers to be Authenticode-signed.
Until now gclo shipped unsigned (exception EX-0002, #57): users saw "Unknown
publisher" in SmartScreen and UAC, and the only origin check was the build-provenance
attestation, which proves which workflow built a file, not who publishes it.

KofTwentyTwo now has an Azure Artifact Signing (formerly Trusted Signing) account
with a completed publisher identity validation and an active Public Trust
certificate profile. The standards' shared `release-velopack.yml` already carries
the signing steps behind its `sign` input: `azure/login` over OIDC, then
`azure/artifact-signing-action` over every published `.exe`/`.dll`, then `vpk pack
--azureTrustedSignFile` so Setup.exe and the update stub are signed with the same
identity.

## Decision

- Turn the shared workflow's `sign` input on. Signing happens after publish and
  before packing; `SHA256SUMS`, the Velopack feed and the attestations are
  computed over the signed files. SHA-256 digests and RFC 3161 timestamps
  (`http://timestamp.acs.microsoft.com`) always.
- Authenticate with **GitHub OIDC**, never a secret: an Entra application
  (`gclo-release-signing`) with a federated credential whose subject is
  `repo:KofTwentyTwo/gclo:environment:release`, so only a job running in the
  tag-restricted `release` environment can obtain a token. The application holds
  exactly one role, *Artifact Signing Certificate Profile Signer*, scoped to the
  certificate profile. Owner or Contributor on the subscription grants no signing
  right and is not given.
- The identity is configuration, not a secret: tenant, client and subscription ids,
  endpoint, account and profile names are `release`-environment variables. Missing
  values fail the Azure login, and nothing unsigned is published.
- A caller-side `verify-signatures` job downloads the published Setup.exe, portable
  and CLI zips and runs `signtool verify /pa /all` plus `Get-AuthenticodeSignature`
  on every gclo executable, checking status, timestamp and the validated publisher
  name (`TRUSTED_SIGNING_SUBJECT`). It cannot stop a publish that already happened,
  but it turns a silently unsigned release into a failed run handled by the broken-
  release runbook; the shared workflow is asked to verify before publishing
  (standards#22 follow-up).
- Local signing is documented for one-off needs only (SignTool + the
  `Microsoft.ArtifactSigning.Client` dlib + an authorized Azure sign-in); releases
  are signed in CI.
- The Azure configuration is scripted in `.github/scripts/Set-ReleaseSigning.ps1`
  (idempotent, run interactively by the owner) so it can be audited and repeated.

## Consequences

- SmartScreen and UAC show the validated publisher; Velopack's installer and update
  stub are signed, so silent self-update no longer rests on TLS plus the GitHub
  account alone.
- Releases depend on Azure being reachable and the certificate profile being
  active; a signing outage fails the release rather than shipping unsigned.
- No PFX, private key or client secret exists anywhere; nothing to rotate except
  the federated credential if the repository or environment is renamed.
- EX-0002 closes for gclo once the first signed release verifies.
