# 0010. Forges beside GitHub: Gitea servers at an address the user names

- Status: accepted
- Date: 2026-11-11
- Issues: #201 (the feature), #202 (the engine, this decision's first use)

## Context

gclo lists and clones the repositories of a GitHub organization or account. The
project itself now lives on a Gitea server, and so do the repositories of people
who want to mirror theirs. Gitea (and Forgejo, which speaks the same API) differs
from GitHub in one way that matters for security: there is no one well-known host.
The server is at an address a user types. That address decides where the account's
token is sent.

The engine already had the right seams: `IRepositoryLister`, `IOrganizationLister`
and `IGitClient` say nothing about GitHub, and since #139 the git client gives the
token only to the host of the clone address it was handed. What was GitHub-specific
were the two lister implementations and the failure type they throw.

## Decision

- **A forge is a pair of listers.** `GiteaRepositoryLister` and
  `GiteaOrganizationLister` implement the existing interfaces and are constructed
  with the server's address. `ForgeKind` names the kinds (`GitHub`, `Gitea`). The
  sync engine, the path recovery and the git client are not changed.
- **One failure type.** `ForgeAccessException` with `ForgeAccessKind` is what a
  failed request to any forge is translated into. `GitHubAccessException` now
  derives from it and keeps its own `Kind`, so code written against it still
  compiles and still catches.
- **No new dependency.** The Gitea API is called with `HttpClient` and read with
  source-generated `System.Text.Json`. Both are tested offline through a fake
  message handler, so none of it is excluded from the coverage gate.
- **The address of a server** (`GiteaServerUrl`) is HTTPS, has no user name or
  password in it, and nothing after its path. A host typed without a scheme is
  taken as HTTPS. `http://` is refused, and there is no setting that allows it.
- **Certificates are validated by the platform, and that cannot be switched off.**
  A server with a certificate from a private authority works once Windows trusts
  that authority.
- **The token goes to the account's server and nowhere else.**
  - Every API request is made to the configured address, with the token in the
    `Authorization` header. Redirects are not followed; a redirect is reported as
    "this is not the server's own address".
  - The address of a next page is never taken from the server (`Link` headers are
    read only for "is there a next page").
  - Clone addresses are built from the configured address, the owner and the
    repository name. The `clone_url` in the server's answers is not read. So the
    host that gets the token for a clone is the host the account names, whatever
    the server's own idea of its address is.
- **What a server sends is bounded**: the size of an answer, the number of pages of
  a list, and the time of a request. A server's own words reach a message only as
  the reason of a refusal, on one line and cut to a length.
- **Paging follows what the server says**, not the size of a page: a Gitea server
  caps the page size, and leaves out of a page what the token may not see, so a
  short or empty page does not end a list. Where a server sends no `Link` header,
  an empty page does.
- **An account that is not an organization** is listed as on GitHub: the token's
  own account gets its own repositories, another account the ones the token can see
  there. From the list of the token's own repositories only those the account owns
  are taken; that list also holds repositories of others the token may work on.

## Alternatives considered

- **A Gitea client package.** One more dependency to track and to trust with the
  token, for six endpoints.
- **Using `clone_url` from the answers.** It would follow a server whose configured
  root address differs from the one the user reaches it by (a reverse proxy), but
  it lets the server name the host that receives the token for git. Building the
  address also makes such a server work.
- **Following redirects on the same host.** Simpler to refuse them all than to
  decide which are the same host.
- **Allowing `http://` for a server on a local network, or a switch to accept any
  certificate.** Both put the token on the wire, or in the hands of whoever answers,
  in exactly the setups where nobody is watching. The supported route for a private
  authority is to trust it in Windows.

## Consequences

- A server reachable only through a sign-in page or a single-sign-on proxy does not
  work: the answer is a page, not the API, and gclo says that the address does not
  answer like a Gitea server.
- A token needs to be allowed to read repositories, organizations and the user
  (`read:repository`, `read:organization`, `read:user`). Without the second, the
  organization list holds the personal account only and an organization is typed
  by hand, as on GitHub.
- The git client uses the same credential form as for GitHub (a fixed user name,
  the token as the password). Gitea accepts a token as the password with any user
  name; this is to be confirmed against a live server before accounts can use a
  Gitea server (#201, step 2).
- The engine's public surface grows (`ForgeKind`, `ForgeAccessException`,
  `ForgeAccessKind`, `GiteaServerUrl`, the two listers), and the base type of
  `GitHubAccessException` changes: a minor version for the package.
- Accounts, the command line and the desktop app follow in their own changes
  (#201). Until then the app and the CLI behave as before.
