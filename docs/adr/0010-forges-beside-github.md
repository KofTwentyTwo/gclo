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
  taken as HTTPS; text that has a scheme-like beginning and is not an address
  (`https:/host` with one slash) is refused instead of being taken for a host.
  `http://` is refused, and there is no setting that allows it. An address that
  is refused is not repeated in the message: it may have been refused for the
  password or token in it.
- **Certificates are validated by the platform, and that cannot be switched off.**
  A server with a certificate from a private authority works once Windows trusts
  that authority.
- **The token goes to the account's server and nowhere else.**
  - Every API request is made to the configured address, with the token in the
    `Authorization` header. Redirects are not followed; a redirect is reported as
    "this is not the server's own address".
  - The address of a next page is never taken from the server (`Link` headers are
    read only for "is there a next page", and only their paging relations).
  - Nothing a server sets is kept: the one client all accounts share uses no
    cookies.
  - Clone addresses are built from the configured address, the owner and the
    repository name. The `clone_url` in the server's answers is not read. So the
    host that gets the token for a clone is the host the account names, whatever
    the server's own idea of its address is.
- **What a server sends is bounded**: the size of an answer, the number of pages of
  a list and of items on a page (so a list has at most 100,000 items), and the
  time of a request, the reading of its answer included. A server's own words
  reach a message only as the reason of a refusal, on one line and cut to a length.
- **Paging follows what the server says**, not the size of a page: a Gitea server
  caps the page size, and leaves out of a page what the token may not see, so a
  short or empty page does not end a list. Where a server sends no `Link` header,
  an empty page does.
- **An account that is not an organization** is listed as on GitHub: the token's
  own account gets its own repositories, another account the ones the token can see
  there. From the list of the token's own repositories only those the account owns
  are taken; that list also holds repositories of others the token may work on.
  A token that may not read organizations is refused at an organization's list
  whatever the name is; the account path is tried then as well, so such a token
  still lists its own account.

### Accounts and the command line (#204)

- **An account names its forge and, for Gitea, its server.** Both are left out
  when they are the default, so a GitHub account is written as it always was.
- **Accounts on another forge are kept in a file of their own**
  (`accounts-forges.json`, beside `accounts.json`, under the same lock). A version
  from before forges reads every account in `accounts.json` as a GitHub account
  and writes the file back with the fields it knows. It would drop the forge and
  the server of a Gitea account, and from then on that account would read as a
  GitHub account in every version: its token would be asked for again and sent to
  GitHub. Two versions on one data folder are ordinary (the app updates itself,
  the command line is a separate download), so such accounts are kept where the
  older version neither reads nor writes. A GitHub account is the one that names
  no forge, and only that one; a record in the second file that names none is not
  read as a GitHub account (gclo writes no such record; one that is found is
  given an empty forge name, which no version knows, and is written back so). The cost is two files where there was one. A change
  itself writes only the file of the account it changes; a file that has to be put
  back, or an account that is in the wrong file, is written before the change, so
  that when it cannot be written the change fails with nothing changed and the
  vault untouched. A name is unique across both files, but an older version does
  not see the second and can add a GitHub account with the name of a Gitea
  account: the command line then refuses the name instead of choosing one of the
  two, until one is renamed in the app.
- **What this version does not know is kept.** The kind of forge is stored as
  text; a name this version does not know (a later version's forge) makes that
  one account unusable here, not the file unreadable, and is written back as it
  was. Fields this version does not know are written back as they were read,
  whoever built the record that is saved. So this version does not do to a later
  one what an older one would do to it. The limits a later version has to keep
  to: a field this version knows keeps its JSON type (text where text is, as for
  `Forge`; another type makes the file unreadable here), and a new field is not
  named like one of this version's computed properties.
- **A token stays with the place it was saved for.** The forge and the server of
  an account are set when it is added and cannot be changed by an edit, in the
  app or on the command line: to move an account, it is removed and added again,
  and its token is entered for the new place. `gclo sync --account` takes no
  `--forge` or `--server`.
- **A token is found only by the account and the place it was saved for.** The
  vault entry of a GitHub account is its id, as ever. For another forge it is made
  from the id, the forge and the server address exactly as the file states them
  (a hash). `AccountsStore.Save` refuses a change of place; this makes the same
  hold for a file changed by hand: at another address the account has no token.
  The price: a token whose account was edited that way stays in Credential Manager
  under a name gclo no longer derives, until the address is put back.
- **Tokens do not cross forges by default.** The default token saved in Settings
  is a GitHub token: an account on another forge has a token of its own, and is
  never handed the default one, whatever its record says. On the command line
  each forge has its own default variable (`GITHUB_TOKEN`, `GITEA_TOKEN`).
- **One place pairs an account with its listers** (`AccountListers`, over
  `ForgeEndpoint`), used by the app and the command line. An account whose stored
  forge or server cannot be used (a file edited by hand) gets listers that ask no
  server anything and fail with the reason.

### The desktop app's wizard (#206)

- **The place is asked first, on step 1, for a new account only.** GitHub is the
  default; a Gitea server needs its address, which is checked by the same rule as
  on the command line (`ForgeEndpoint.TryCreate`). Step 2 names the address as it
  was normalized, in full, where the token is asked for: that is the address the
  token goes to. An edit shows where the
  account is and that it stays there. A Quick Sync connection saved as an account
  is an account on the place the connection was on (#209).
- **The wizard has the lister of one place at a time.** The shell gives it a
  lister per place; step 1 takes the lister of the place that was chosen, and step
  2 has no other to send the token to. A wizard that was given one lister (an
  edit, a saved Quick Sync connection) offers no choice.
- **What was entered for one place is not carried to another.** When step 1 is
  left with another place than before, the typed token, the organization and the
  list of organizations are emptied, and the default token (a GitHub token) is
  neither offered nor preselected for a Gitea server.
- **Texts follow the place.** The page a token is made on is the one of the
  chosen place (`ForgeTexts`); step 2 names the place the token is checked with.

### Quick Sync and the start screen (#209)

GitHub and Gitea are equal options wherever the app asks for a place, with the
same two labels. Quick Sync, the card the app first opens on, is where that
matters most.

- **The place is chosen on the card, and nothing is chosen for the user.** On a
  first run neither GitHub nor Gitea is selected, and the token box takes a token
  only once one is. A token cannot be pasted for a place nobody chose.
- **Nothing is sent until the user asks.** A token that is typed, pasted, or
  filled in from a saved default goes nowhere by itself. It leaves the machine
  when the user presses Find (the organization list) or Load repositories, and
  it goes to the listers of the place that is on the card at that moment. An
  account's page still lists and loads when it is opened: its token was saved for
  its place.
- **A server address is taken when its box is left**, not while it is typed, so no
  token is sent to half an address; the card then shows it as it was normalized.
  While the box holds another text than the address that was taken, and while
  the card names no usable address, nothing can be looked up, loaded or synced,
  whatever starts it (a button, a keyboard shortcut).
- **What belongs to one place is forgotten when another is chosen:** the token,
  the organization, and the table. The repositories of the table have clone
  addresses on the other host; they are not left there to be synced with a token
  of the new place.
- **The card opens the way the last connection that worked left it:** place,
  server address, organization, target folder, kept in `settings.json`. Never a
  token. An older gclo that rewrites `settings.json` drops these fields; the card
  then opens as on a first run, which costs a choice and nothing else.
- **The app opens where it was left:** the workspace that was open last; with
  nothing recorded, the first account; with no account, Quick Sync.

## Alternatives considered

- **A Gitea client package.** One more dependency to track and to trust with the
  token, for six endpoints.
- **Using `clone_url` from the answers.** It would follow a server whose configured
  root address differs from the one the user reaches it by (a reverse proxy), but
  it lets the server name the host that receives the token for git. Building the
  address also makes such a server work.
- **Following redirects on the same host.** Simpler to refuse them all than to
  decide which are the same host.
- **Letting an edit change an account's server**, with the token entered again.
  The rule "the token is asked for again" would have to hold in the wizard, the
  command line and the store; "it cannot be changed" holds in the store alone.
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
  name; this is still to be confirmed against a live server, before the release
  that brings Gitea support (#201).
- The engine's public surface grows (`ForgeKind`, `ForgeAccessException`,
  `ForgeAccessKind`, `GiteaServerUrl`, the two listers), and the base type of
  `GitHubAccessException` changes: a minor version for the package. Code that
  catches either type, or reads `Kind`, is not affected; code that looks `Kind`
  up by name through reflection now finds two properties.
- A server answers a renamed organization or account with a redirect, which gclo
  does not follow: the message says to use the new name.
- Accounts, the command line and the desktop app came in their own changes
  (#204, #206; the sections above).
