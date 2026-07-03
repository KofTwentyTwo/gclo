using Octokit;

namespace gclo.Engine;

/// <summary>Lists org repositories through the GitHub REST API via Octokit.</summary>
public sealed class GitHubRepositoryLister : IRepositoryLister
{
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

        var repositories = new List<Repository>();
        try
        {
            // Page manually (instead of one GetAllForOrg call) so cancellation is
            // honored between round trips on orgs with hundreds of repositories.
            for (int page = 1; ; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = await client.Repository
                    .GetAllForOrg(organization, new ApiOptions { PageSize = 100, PageCount = 1, StartPage = page })
                    .ConfigureAwait(false);
                repositories.AddRange(batch);
                if (batch.Count < 100)
                {
                    break;
                }
            }
        }
        catch (AuthorizationException ex)
        {
            throw new InvalidOperationException(
                "GitHub rejected the token (401). Check the PAT and make sure it has 'repo' (classic) or organization repository read access (fine-grained).", ex);
        }
        catch (NotFoundException ex)
        {
            throw new InvalidOperationException(
                $"Organization '{organization}' was not found (404), or the token cannot see it.", ex);
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
