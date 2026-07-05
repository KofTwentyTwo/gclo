using Octokit;

namespace gclo.Engine;

/// <summary>Lists the authenticated user's organizations through the GitHub REST API.</summary>
public sealed class GitHubOrganizationLister : IOrganizationLister
{
    private readonly Func<string, IGitHubGateway> _gatewayFactory;

    /// <summary>Production wiring: a fresh Octokit-backed gateway per call's token.</summary>
    public GitHubOrganizationLister()
        : this(CreateOctokitGateway)
    {
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(
        Justification = "Production wiring to the live GitHub API; the offline suite injects a fake gateway.")]
    private static IGitHubGateway CreateOctokitGateway(string token) => new OctokitGateway(token);

    /// <summary>Test seam: substitute the GitHub API with a fake gateway.</summary>
    internal GitHubOrganizationLister(Func<string, IGitHubGateway> gatewayFactory)
        => _gatewayFactory = gatewayFactory ?? throw new ArgumentNullException(nameof(gatewayFactory));

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> ListOrganizationsAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        cancellationToken.ThrowIfCancellationRequested();

        IGitHubGateway gateway = _gatewayFactory(token);

        string userLogin;
        IReadOnlyList<string> organizations;
        try
        {
            // The token's own account is a valid sync target too (personal repos
            // live under /users, not /orgs), so it heads the list.
            userLogin = await gateway.GetCurrentUserLoginAsync().ConfigureAwait(false);

            try
            {
                organizations = await gateway.GetOrganizationLoginsAsync().ConfigureAwait(false);
            }
            catch (ForbiddenException ex) when (ex is not RateLimitExceededException)
            {
                // Degraded mode: classic PATs without the read:org scope get 403
                // from /user/orgs even though the token is otherwise fine. The
                // personal account is still a usable sync target, so return just
                // that instead of failing the whole listing.
                return [userLogin];
            }
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
            .Where(login => !string.Equals(login, userLogin, StringComparison.OrdinalIgnoreCase))
            .OrderBy(login => login, StringComparer.OrdinalIgnoreCase));
        return result;
    }
}
