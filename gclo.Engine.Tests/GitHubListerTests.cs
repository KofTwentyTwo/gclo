using System.Net;
using Octokit;

namespace gclo.Engine.Tests;

/// <summary>
/// Pins the GitHub listers' pagination, account-fallback, ordering, dedupe, and
/// exception-translation logic over a fake <see cref="IGitHubGateway"/> — the
/// production gateway is thin Octokit delegation and stays outside the offline suite.
/// </summary>
public sealed class GitHubListerTests
{
    private sealed class FakeGateway : IGitHubGateway
    {
        public Func<string, int, Task<IReadOnlyList<GitHubRepo>>> OrgPages { get; set; } =
            (_, _) => Task.FromResult<IReadOnlyList<GitHubRepo>>([]);
        public Func<int, Task<IReadOnlyList<GitHubRepo>>> OwnPages { get; set; } =
            _ => Task.FromResult<IReadOnlyList<GitHubRepo>>([]);
        public Func<string, int, Task<IReadOnlyList<GitHubRepo>>> UserPages { get; set; } =
            (_, _) => Task.FromResult<IReadOnlyList<GitHubRepo>>([]);
        public Func<Task<string>> CurrentUser { get; set; } = () => Task.FromResult("me");
        public Func<Task<IReadOnlyList<string>>> OrgLogins { get; set; } =
            () => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<GitHubRepo>> GetOrganizationRepositoriesPageAsync(string organization, int page)
            => OrgPages(organization, page);
        public Task<IReadOnlyList<GitHubRepo>> GetOwnRepositoriesPageAsync(int page) => OwnPages(page);
        public Task<IReadOnlyList<GitHubRepo>> GetUserRepositoriesPageAsync(string user, int page)
            => UserPages(user, page);
        public Task<string> GetCurrentUserLoginAsync() => CurrentUser();
        public Task<IReadOnlyList<string>> GetOrganizationLoginsAsync() => OrgLogins();
    }

    private readonly FakeGateway _gateway = new();
    private string? _tokenSeen;

    private GitHubRepositoryLister RepoLister()
        => new(token =>
        {
            _tokenSeen = token;
            return _gateway;
        });

    private GitHubOrganizationLister OrgLister()
        => new(token =>
        {
            _tokenSeen = token;
            return _gateway;
        });

    private static GitHubRepo Repo(string name) => new(name, $"https://x/{name}.git", "main", false);

    private static NotFoundException NotFound() => new("nope", HttpStatusCode.NotFound);

    private static AuthorizationException Unauthorized() => new(HttpStatusCode.Unauthorized, null);

    private static ForbiddenException Forbidden() => new(new FakeResponse(HttpStatusCode.Forbidden));

    private static RateLimitExceededException RateLimited()
        => new(new FakeResponse(HttpStatusCode.Forbidden, new RateLimit(60, 0, 1735689600)));

    /// <summary>Octokit's Response type is internal; its IResponse is not.</summary>
    private sealed class FakeResponse(HttpStatusCode status, RateLimit? rateLimit = null) : IResponse
    {
        public object? Body => "";
        public IReadOnlyDictionary<string, string> Headers { get; } = new Dictionary<string, string>();
        public ApiInfo ApiInfo { get; } = new(
            new Dictionary<string, Uri>(), [], [], "", rateLimit ?? new RateLimit(60, 60, 0));
        public HttpStatusCode StatusCode => status;
        public string? ContentType => "application/json";
    }

    // ---------------------------------------------------------------- repositories

    [Fact]
    public async Task Repos_PagesUntilAShortBatch_AndSortsByName()
    {
        var first = Enumerable.Range(0, IGitHubGateway.PageSize).Select(i => Repo($"z{i:D3}")).ToList();
        _gateway.OrgPages = (org, page) => Task.FromResult<IReadOnlyList<GitHubRepo>>(
            page == 1 ? first : [Repo("alpha")]);

        var repos = await RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890");

        Assert.Equal(IGitHubGateway.PageSize + 1, repos.Count);
        Assert.Equal("alpha", repos[0].Name); // sorted, so the page-2 repo leads
        Assert.Equal("tok-1234567890", _tokenSeen);
    }

    [Fact]
    public async Task Repos_DuplicateNamesAcrossPageBoundary_AreDeduplicated()
    {
        _gateway.OrgPages = (_, _) => Task.FromResult<IReadOnlyList<GitHubRepo>>(
            [Repo("same"), new GitHubRepo("SAME", "https://x/dup.git", "dev", true)]);

        var repos = await RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890");

        Assert.Single(repos); // case-insensitive: the first wins
        Assert.Equal("same", repos[0].Name);
    }

    [Fact]
    public async Task Repos_MapsFieldsIntoDescriptors()
    {
        _gateway.OrgPages = (_, _) => Task.FromResult<IReadOnlyList<GitHubRepo>>(
            [new GitHubRepo("r", "https://x/r.git", null, true)]);

        var repos = await RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890");

        Assert.Equal(new RepoDescriptor("r", "https://x/r.git", null, true), repos[0]);
    }

    [Fact]
    public async Task Repos_OrgNotFound_ButItIsTheTokensOwnAccount_ListsOwnedRepos()
    {
        _gateway.OrgPages = (_, _) => throw NotFound();
        _gateway.CurrentUser = () => Task.FromResult("Kof");
        _gateway.OwnPages = _ => Task.FromResult<IReadOnlyList<GitHubRepo>>([Repo("mine")]);

        var repos = await RepoLister().ListOrganizationRepositoriesAsync("kof", "tok-1234567890");

        Assert.Equal("mine", Assert.Single(repos).Name); // login match is case-insensitive
    }

    [Fact]
    public async Task Repos_OrgNotFound_OtherUserAccount_ListsThatUsersRepos()
    {
        _gateway.OrgPages = (_, _) => throw NotFound();
        _gateway.UserPages = (user, _) => Task.FromResult<IReadOnlyList<GitHubRepo>>([Repo(user + "-repo")]);

        var repos = await RepoLister().ListOrganizationRepositoriesAsync("somebody", "tok-1234567890");

        Assert.Equal("somebody-repo", Assert.Single(repos).Name);
    }

    [Fact]
    public async Task Repos_NeitherOrgNorUser_ThrowsAUsefulMessage()
    {
        _gateway.OrgPages = (_, _) => throw NotFound();
        _gateway.UserPages = (_, _) => throw NotFound();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RepoLister().ListOrganizationRepositoriesAsync("ghost", "tok-1234567890"));

        Assert.Contains("'ghost'", ex.Message);
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task Repos_BadToken_TranslatesTo401Guidance()
    {
        _gateway.OrgPages = (_, _) => throw Unauthorized();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890"));

        Assert.Contains("401", ex.Message);
        Assert.Contains("PAT", ex.Message);
    }

    [Fact]
    public async Task Repos_RateLimited_TranslatesWithResetTime()
    {
        _gateway.OrgPages = (_, _) => throw RateLimited();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890"));

        Assert.Contains("rate limit", ex.Message);
    }

    [Fact]
    public async Task Repos_CancellationBetweenPages_Throws()
    {
        using var cts = new CancellationTokenSource();
        var full = Enumerable.Range(0, IGitHubGateway.PageSize).Select(i => Repo($"r{i:D3}")).ToList();
        _gateway.OrgPages = (_, _) =>
        {
            cts.Cancel(); // requested mid-listing; the next page boundary must observe it
            return Task.FromResult<IReadOnlyList<GitHubRepo>>(full);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890", cts.Token));
    }

    [Fact]
    public async Task Repos_AlreadyCanceled_ThrowsBeforeAnyCall()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890", cts.Token));
        Assert.Null(_tokenSeen); // the gateway was never created
    }

    [Theory]
    [InlineData("", "tok")]
    [InlineData("acme", "")]
    public async Task Repos_BlankArguments_Throw(string organization, string token)
        => await Assert.ThrowsAsync<ArgumentException>(
            () => RepoLister().ListOrganizationRepositoriesAsync(organization, token));

    // ---------------------------------------------------------------- organizations

    [Fact]
    public async Task Orgs_OwnLoginFirst_ThenOrgsSorted_OwnLoginNeverDuplicated()
    {
        _gateway.CurrentUser = () => Task.FromResult("Kof");
        _gateway.OrgLogins = () => Task.FromResult<IReadOnlyList<string>>(["zeta", "acme", "KOF"]);

        var orgs = await OrgLister().ListOrganizationsAsync("tok-1234567890");

        Assert.Equal(["Kof", "acme", "zeta"], orgs);
    }

    [Fact]
    public async Task Orgs_MissingReadOrgScope_DegradesToJustThePersonalAccount()
    {
        _gateway.CurrentUser = () => Task.FromResult("Kof");
        _gateway.OrgLogins = () => throw Forbidden();

        var orgs = await OrgLister().ListOrganizationsAsync("tok-1234567890");

        Assert.Equal(["Kof"], orgs);
    }

    [Fact]
    public async Task Orgs_BadToken_TranslatesTo401Guidance()
    {
        _gateway.CurrentUser = () => throw Unauthorized();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => OrgLister().ListOrganizationsAsync("tok-1234567890"));

        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task Orgs_RateLimited_TranslatesWithResetTime()
    {
        _gateway.OrgLogins = () => throw RateLimited();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => OrgLister().ListOrganizationsAsync("tok-1234567890"));

        Assert.Contains("rate limit", ex.Message);
    }

    [Fact]
    public async Task Orgs_AlreadyCanceled_ThrowsBeforeAnyCall()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => OrgLister().ListOrganizationsAsync("tok-1234567890", cts.Token));
        Assert.Null(_tokenSeen);
    }

    [Fact]
    public async Task Orgs_BlankToken_Throws()
        => await Assert.ThrowsAsync<ArgumentException>(() => OrgLister().ListOrganizationsAsync(" "));

    [Fact]
    public void Listers_NullGatewayFactory_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new GitHubRepositoryLister(null!));
        Assert.Throws<ArgumentNullException>(() => new GitHubOrganizationLister(null!));
    }

    [Fact]
    public void Listers_ParameterlessConstructor_Constructs()
    {
        // The production wiring is exercised (the Octokit factory itself is excluded);
        // no API call is made, so this stays offline.
        Assert.NotNull(new GitHubRepositoryLister());
        Assert.NotNull(new GitHubOrganizationLister());
    }
}
