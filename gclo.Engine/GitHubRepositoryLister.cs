using Octokit;

namespace gclo.Engine;

/// <summary>Lists org repositories through the GitHub REST API via Octokit.</summary>
public sealed class GitHubRepositoryLister : IRepositoryLister
{
    private readonly Func<string, IGitHubGateway> _gatewayFactory;

    /// <summary>Production wiring: a fresh Octokit-backed gateway per call's token.</summary>
    public GitHubRepositoryLister()
        : this(CreateOctokitGateway)
    {
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(
        Justification = "Production wiring to the live GitHub API; the offline suite injects a fake gateway.")]
    private static IGitHubGateway CreateOctokitGateway(string token) => new OctokitGateway(token);

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
                // /orgs/{name}/repos 404s for user accounts: the token's own account
                // gets its owned repos (including private); any other user account
                // yields the repos the token can see there (public).
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
