using Octokit;

namespace gclo.Engine;

/// <summary>Lists org repositories through the GitHub REST API via Octokit.</summary>
public sealed class GitHubRepositoryLister : IRepositoryLister
{
    private readonly Func<string, IGitHubGateway> _gatewayFactory;

    /// <summary>Production wiring: the process-wide Octokit gateway for the call's token (connection reuse).</summary>
    public GitHubRepositoryLister()
        : this(OctokitGatewayCache.Get)
    {
    }

    /// <summary>Test seam: substitute the GitHub API with a fake gateway.</summary>
    internal GitHubRepositoryLister(Func<string, IGitHubGateway> gatewayFactory)
        => _gatewayFactory = gatewayFactory ?? throw new ArgumentNullException(nameof(gatewayFactory));

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RepoDescriptor>> ListOrganizationRepositoriesAsync(
        string organization, string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organization);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        cancellationToken.ThrowIfCancellationRequested();

        IGitHubGateway gateway = _gatewayFactory(token);

        // Pages manually (instead of one GetAll* call) so cancellation is honored
        // between round trips on owners with hundreds of repositories.
        async Task<List<GitHubRepo>> PageAsync(Func<int, Task<IReadOnlyList<GitHubRepo>>> fetchPage)
        {
            var all = new List<GitHubRepo>();
            for (int page = 1; ; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = await fetchPage(page).ConfigureAwait(false);
                all.AddRange(batch);
                if (batch.Count < IGitHubGateway.PageSize)
                {
                    break;
                }
            }
            return all;
        }

        List<GitHubRepo> repositories;
        try
        {
            try
            {
                repositories = await PageAsync(
                    page => gateway.GetOrganizationRepositoriesPageAsync(organization, page))
                    .ConfigureAwait(false);
            }
            catch (NotFoundException)
            {
                // /orgs/{name}/repos 404s for user accounts — but ALSO for a real
                // organization the token cannot see (an ungranted fine-grained PAT, or
                // SSO not authorized). Falling through to /users/{name}/repos for an
                // organization would list only its public repositories and report a
                // green sync that silently omitted the rest, so the account kind is
                // resolved first and an invisible organization is an error (#31).
                GitHubAccountKind kind = await gateway.GetAccountKindAsync(organization).ConfigureAwait(false);
                if (kind == GitHubAccountKind.Organization)
                {
                    throw new InvalidOperationException(
                        $"'{organization}' is an organization, but this token cannot see its repositories (404). "
                        + "Grant the PAT access to the organization — for a fine-grained token choose it as the resource owner, "
                        + "for a classic token authorize it for SSO — then try again.");
                }
                if (kind == GitHubAccountKind.NotFound)
                {
                    throw new InvalidOperationException(
                        $"'{organization}' was found neither as an organization nor as a user account (404). Check the spelling.");
                }

                // A user account: the token's own account gets its owned repos
                // (including private); any other user account yields the repos the
                // token can see there (public).
                string currentUser = await gateway.GetCurrentUserLoginAsync().ConfigureAwait(false);
                if (string.Equals(currentUser, organization, StringComparison.OrdinalIgnoreCase))
                {
                    repositories = await PageAsync(gateway.GetOwnRepositoriesPageAsync)
                        .ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        repositories = await PageAsync(
                            page => gateway.GetUserRepositoriesPageAsync(organization, page))
                            .ConfigureAwait(false);
                    }
                    catch (NotFoundException ex)
                    {
                        throw new InvalidOperationException(
                            $"'{organization}' was found neither as an organization nor as a user account (404) — or the token cannot see it.", ex);
                    }
                }
            }
        }
        catch (AuthorizationException ex)
        {
            throw new InvalidOperationException(
                "GitHub rejected the token (401). Check the PAT and make sure it has 'repo' (classic) or repository read access (fine-grained).", ex);
        }
        catch (RateLimitExceededException ex)
        {
            throw new InvalidOperationException(
                $"GitHub API rate limit exceeded; it resets at {ex.Reset:u}.", ex);
        }
        catch (SecondaryRateLimitExceededException ex)
        {
            // A burst of requests, not a scope problem; Octokit models it beside (not
            // under) AbuseException, so both get the same translation.
            throw new InvalidOperationException(
                "GitHub's secondary rate limit was hit (too many requests in a short time); retry in a minute.", ex);
        }
        catch (AbuseException ex)
        {
            throw new InvalidOperationException(
                $"GitHub's secondary rate limit was hit (too many requests in a short time); retry in {ex.RetryAfterSeconds ?? 60} seconds.", ex);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return repositories
            // A repo created mid-listing shifts GitHub's offset pagination and can
            // repeat a boundary item; duplicates would race two clones into one folder.
            .DistinctBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new RepoDescriptor(r.Name, r.CloneUrl, r.DefaultBranch, r.IsArchived))
            .ToList();
    }
}
