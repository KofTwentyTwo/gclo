using Octokit;

namespace gclo.Engine;

/// <summary>Lists org repositories through the GitHub REST API via Octokit.</summary>
public sealed class GitHubRepositoryLister : IRepositoryLister
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<RepoDescriptor>> ListOrganizationRepositoriesAsync(
        string organization, string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organization);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        cancellationToken.ThrowIfCancellationRequested();

        var client = new GitHubClient(new ProductHeaderValue("gclo"))
        {
            Credentials = new Credentials(token),
        };

        // Pages manually (instead of one GetAll* call) so cancellation is honored
        // between round trips on owners with hundreds of repositories.
        async Task<List<Repository>> PageAsync(Func<ApiOptions, Task<IReadOnlyList<Repository>>> fetchPage)
        {
            var all = new List<Repository>();
            for (int page = 1; ; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = await fetchPage(new ApiOptions { PageSize = 100, PageCount = 1, StartPage = page })
                    .ConfigureAwait(false);
                all.AddRange(batch);
                if (batch.Count < 100)
                {
                    break;
                }
            }
            return all;
        }

        List<Repository> repositories;
        try
        {
            try
            {
                repositories = await PageAsync(o => client.Repository.GetAllForOrg(organization, o))
                    .ConfigureAwait(false);
            }
            catch (NotFoundException)
            {
                // /orgs/{name}/repos 404s for user accounts: the token's own account
                // gets its owned repos (including private); any other user account
                // yields the repos the token can see there (public).
                var currentUser = await client.User.Current().ConfigureAwait(false);
                if (string.Equals(currentUser.Login, organization, StringComparison.OrdinalIgnoreCase))
                {
                    var owned = new RepositoryRequest { Affiliation = RepositoryAffiliation.Owner };
                    repositories = await PageAsync(o => client.Repository.GetAllForCurrent(owned, o))
                        .ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        repositories = await PageAsync(o => client.Repository.GetAllForUser(organization, o))
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
            .Select(r => new RepoDescriptor(r.Name, r.CloneUrl, r.DefaultBranch, r.Archived))
            .ToList();
    }
}
