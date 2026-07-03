using Octokit;

namespace gclo.Engine;

/// <summary>Lists the authenticated user's organizations through the GitHub REST API.</summary>
public sealed class GitHubOrganizationLister : IOrganizationLister
{
    public async Task<IReadOnlyList<string>> ListOrganizationsAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        cancellationToken.ThrowIfCancellationRequested();

        var client = new GitHubClient(new ProductHeaderValue("gclo"))
        {
            Credentials = new Credentials(token),
        };

        IReadOnlyList<Organization> organizations;
        try
        {
            organizations = await client.Organization
                .GetAllForCurrent(new ApiOptions { PageSize = 100 })
                .ConfigureAwait(false);
        }
        catch (AuthorizationException ex)
        {
            throw new InvalidOperationException("GitHub rejected the token (401). Check the PAT.", ex);
        }
        catch (RateLimitExceededException ex)
        {
            throw new InvalidOperationException($"GitHub API rate limit exceeded; it resets at {ex.Reset:u}.", ex);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return organizations
            .Select(o => o.Login)
            .OrderBy(login => login, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
