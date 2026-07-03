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

        string userLogin;
        IReadOnlyList<Organization> organizations;
        try
        {
            // The token's own account is a valid sync target too (personal repos
            // live under /users, not /orgs), so it heads the list.
            var currentUser = await client.User.Current().ConfigureAwait(false);
            userLogin = currentUser.Login;

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

        var result = new List<string> { userLogin };
        result.AddRange(organizations
            .Select(o => o.Login)
            .Where(login => !string.Equals(login, userLogin, StringComparison.OrdinalIgnoreCase))
            .OrderBy(login => login, StringComparer.OrdinalIgnoreCase));
        return result;
    }
}
