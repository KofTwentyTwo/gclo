using System.Diagnostics.CodeAnalysis;
using Octokit;

namespace gclo.Engine;

/// <summary>One repository as the GitHub API describes it, in the fields gclo uses.</summary>
internal readonly record struct GitHubRepo(string Name, string CloneUrl, string? DefaultBranch, bool IsArchived);

/// <summary>
/// The seam between the listers and the GitHub REST API: exactly the calls gclo
/// makes, returning plain shapes. Implementations surface Octokit's exception
/// types unchanged (<see cref="NotFoundException"/>, <see cref="AuthorizationException"/>,
/// <see cref="ForbiddenException"/>, <see cref="RateLimitExceededException"/>) —
/// translating them into user-facing errors is the listers' job, and that logic
/// is what the tests pin.
/// </summary>
internal interface IGitHubGateway
{
    /// <summary>Repositories per page; a batch smaller than this ends the paging loop.</summary>
    const int PageSize = 100;

    /// <summary>One page of an organization's repositories (1-based page number).</summary>
    Task<IReadOnlyList<GitHubRepo>> GetOrganizationRepositoriesPageAsync(string organization, int page);

    /// <summary>One page of the token's own repositories (owner affiliation).</summary>
    Task<IReadOnlyList<GitHubRepo>> GetOwnRepositoriesPageAsync(int page);

    /// <summary>One page of another user account's visible repositories.</summary>
    Task<IReadOnlyList<GitHubRepo>> GetUserRepositoriesPageAsync(string user, int page);

    /// <summary>The login of the account the token authenticates as.</summary>
    Task<string> GetCurrentUserLoginAsync();

    /// <summary>Logins of the organizations visible to the token.</summary>
    Task<IReadOnlyList<string>> GetOrganizationLoginsAsync();
}

/// <summary>
/// The production <see cref="IGitHubGateway"/> over Octokit. Pure delegation with
/// no branching of its own, which is why it is excluded from the coverage gate:
/// exercising these lines requires the live GitHub API, and the test suite is
/// offline by policy (see CONTRIBUTING).
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Thin Octokit delegation; needs the live GitHub API.")]
internal sealed class OctokitGateway : IGitHubGateway
{
    private readonly GitHubClient _client;

    public OctokitGateway(string token)
        => _client = new GitHubClient(new ProductHeaderValue("gclo"))
        {
            Credentials = new Credentials(token),
        };

    public async Task<IReadOnlyList<GitHubRepo>> GetOrganizationRepositoriesPageAsync(string organization, int page)
        => Map(await _client.Repository.GetAllForOrg(organization, Page(page)).ConfigureAwait(false));

    public async Task<IReadOnlyList<GitHubRepo>> GetOwnRepositoriesPageAsync(int page)
    {
        var owned = new RepositoryRequest { Affiliation = RepositoryAffiliation.Owner };
        return Map(await _client.Repository.GetAllForCurrent(owned, Page(page)).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<GitHubRepo>> GetUserRepositoriesPageAsync(string user, int page)
        => Map(await _client.Repository.GetAllForUser(user, Page(page)).ConfigureAwait(false));

    public async Task<string> GetCurrentUserLoginAsync()
        => (await _client.User.Current().ConfigureAwait(false)).Login;

    public async Task<IReadOnlyList<string>> GetOrganizationLoginsAsync()
    {
        var organizations = await _client.Organization
            .GetAllForCurrent(new ApiOptions { PageSize = IGitHubGateway.PageSize })
            .ConfigureAwait(false);
        return organizations.Select(o => o.Login).ToList();
    }

    private static ApiOptions Page(int page)
        => new() { PageSize = IGitHubGateway.PageSize, PageCount = 1, StartPage = page };

    private static List<GitHubRepo> Map(IReadOnlyList<Repository> repositories)
        => repositories.Select(r => new GitHubRepo(r.Name, r.CloneUrl, r.DefaultBranch, r.Archived)).ToList();
}
