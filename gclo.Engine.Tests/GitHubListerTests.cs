/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

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

      public Func<string, Task<GitHubAccountKind>> AccountKind { get; set; } =
          _ => Task.FromResult(GitHubAccountKind.User);



      public Task<IReadOnlyList<GitHubRepo>> GetOrganizationRepositoriesPageAsync(string organization, int page)
          => OrgPages(organization, page);



      public Task<IReadOnlyList<GitHubRepo>> GetOwnRepositoriesPageAsync(int page) => OwnPages(page);



      public Task<IReadOnlyList<GitHubRepo>> GetUserRepositoriesPageAsync(string user, int page)
          => UserPages(user, page);



      public Task<string> GetCurrentUserLoginAsync() => CurrentUser();



      public Task<IReadOnlyList<string>> GetOrganizationLoginsAsync() => OrgLogins();



      public Task<GitHubAccountKind> GetAccountKindAsync(string login) => AccountKind(login);
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



   private static SecondaryRateLimitExceededException SecondaryRateLimited()
       => new(new FakeResponse(HttpStatusCode.Forbidden));



   private static LoginAttemptsExceededException LoginAttemptsExceeded()
       => new(new FakeResponse(HttpStatusCode.Forbidden));



   private static AbuseException Abuse() => new(new FakeResponse(HttpStatusCode.Forbidden));



   /// <summary>Octokit's Response type is internal; its IResponse is not.</summary>
   private sealed class FakeResponse(HttpStatusCode status, RateLimit? rateLimit = null, string body = "") : IResponse
   {
      public object? Body => body;



      public IReadOnlyDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

      public ApiInfo ApiInfo { get; } = new(
          new Dictionary<string, Uri>(StringComparer.Ordinal), [], [], "", rateLimit ?? new RateLimit(60, 60, 0));



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

      IReadOnlyList<RepoDescriptor> repos = await RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890");

      Assert.Equal(IGitHubGateway.PageSize + 1, repos.Count);
      Assert.Equal("alpha", repos[0].Name); // sorted, so the page-2 repo leads
      Assert.Equal("tok-1234567890", _tokenSeen);
   }



   [Fact]
   public async Task Repos_DuplicateNamesAcrossPageBoundary_AreDeduplicated()
   {
      _gateway.OrgPages = (_, _) => Task.FromResult<IReadOnlyList<GitHubRepo>>(
          [Repo("same"), new GitHubRepo("SAME", "https://x/dup.git", "dev", true)]);

      IReadOnlyList<RepoDescriptor> repos = await RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890");

      Assert.Single(repos); // case-insensitive: the first wins
      Assert.Equal("same", repos[0].Name);
   }



   [Fact]
   public async Task Repos_MapsFieldsIntoDescriptors()
   {
      _gateway.OrgPages = (_, _) => Task.FromResult<IReadOnlyList<GitHubRepo>>(
          [new GitHubRepo("r", "https://x/r.git", null, true)]);

      IReadOnlyList<RepoDescriptor> repos = await RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890");

      Assert.Equal(new RepoDescriptor("r", "https://x/r.git", null, true), repos[0]);
   }



   [Fact]
   public async Task Repos_OrgNotFound_ButItIsTheTokensOwnAccount_ListsOwnedRepos()
   {
      _gateway.OrgPages = (_, _) => throw NotFound();
      _gateway.CurrentUser = () => Task.FromResult("Kof");
      _gateway.OwnPages = _ => Task.FromResult<IReadOnlyList<GitHubRepo>>([Repo("mine")]);

      IReadOnlyList<RepoDescriptor> repos = await RepoLister().ListOrganizationRepositoriesAsync("kof", "tok-1234567890");

      Assert.Equal("mine", Assert.Single(repos).Name); // login match is case-insensitive
   }



   [Theory]
   [InlineData("../..")]
   [InlineData("..")]
   [InlineData("acme/other")]
   [InlineData(@"acme\other")]
   [InlineData("C:acme")]
   public async Task Repos_NameThatIsNotOnePlainName_IsRefusedBeforeAnyRequest(string organization)
   {
      bool asked = false;
      _gateway.OrgPages = (_, _) => { asked = true; return Task.FromResult<IReadOnlyList<GitHubRepo>>([]); };
      _gateway.AccountKind = _ => { asked = true; return Task.FromResult(GitHubAccountKind.User); };

      GitHubAccessException ex = await Assert.ThrowsAsync<GitHubAccessException>(
          () => RepoLister().ListOrganizationRepositoriesAsync(organization, "tok-1234567890"));

      Assert.Equal(GitHubAccessKind.NotFound, ex.Kind);
      Assert.Contains($"'{organization}' is not an organization or account name", ex.Message);
      Assert.False(asked, "no request may be made for a name that is not a name");
   }



   [Fact]
   public async Task Repos_OrgNotFound_ButItIsAnOrganizationTheTokenCannotSee_FailsInsteadOfListingPublicRepos()
   {
      // The worst failure mode for a mirror tool: a green sync of the 12 public
      // repos of a 400-repo org. The account kind is resolved before any fallback.
      _gateway.OrgPages = (_, _) => throw NotFound();
      _gateway.AccountKind = _ => Task.FromResult(GitHubAccountKind.Organization);
      bool userReposAsked = false;
      _gateway.UserPages = (_, _) => { userReposAsked = true; return Task.FromResult<IReadOnlyList<GitHubRepo>>([Repo("public-only")]); };

      GitHubAccessException ex = await Assert.ThrowsAsync<GitHubAccessException>(
          () => RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890"));

      Assert.Contains("'acme' is an organization", ex.Message);
      Assert.Contains("cannot see its repositories", ex.Message);
      Assert.Equal(GitHubAccessKind.Forbidden, ex.Kind);
      Assert.Contains("resource owner", ex.Message);
      Assert.False(userReposAsked, "the public-repos endpoint must never be consulted for an organization");
   }



   [Fact]
   public async Task Repos_OrgNotFound_AndNoSuchAccount_FailsWithSpellingHint()
   {
      _gateway.OrgPages = (_, _) => throw NotFound();
      _gateway.AccountKind = _ => Task.FromResult(GitHubAccountKind.NotFound);

      GitHubAccessException ex = await Assert.ThrowsAsync<GitHubAccessException>(
          () => RepoLister().ListOrganizationRepositoriesAsync("typo", "tok-1234567890"));

      Assert.Contains("neither as an organization nor as a user account", ex.Message);
      Assert.Contains("Check the spelling", ex.Message);
      Assert.Equal(GitHubAccessKind.NotFound, ex.Kind);
   }



   [Fact]
   public async Task Repos_SecondaryRateLimit_TranslatesWithRetryHint()
   {
      _gateway.OrgPages = (_, _) => throw SecondaryRateLimited();

      GitHubAccessException ex = await Assert.ThrowsAsync<GitHubAccessException>(
          () => RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890"));

      Assert.Contains("secondary rate limit", ex.Message);
      Assert.Contains("retry in", ex.Message);
      Assert.IsType<SecondaryRateLimitExceededException>(ex.InnerException);
      Assert.Equal(GitHubAccessKind.RateLimited, ex.Kind);

      _gateway.OrgPages = (_, _) => throw Abuse();
      GitHubAccessException abuse = await Assert.ThrowsAsync<GitHubAccessException>(
          () => RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890"));
      Assert.Contains("retry in 60 seconds", abuse.Message);
   }



   [Fact]
   public async Task Repos_OrgNotFound_OtherUserAccount_ListsThatUsersRepos()
   {
      _gateway.OrgPages = (_, _) => throw NotFound();
      _gateway.UserPages = (user, _) => Task.FromResult<IReadOnlyList<GitHubRepo>>([Repo(user + "-repo")]);

      IReadOnlyList<RepoDescriptor> repos = await RepoLister().ListOrganizationRepositoriesAsync("somebody", "tok-1234567890");

      Assert.Equal("somebody-repo", Assert.Single(repos).Name);
   }



   [Fact]
   public async Task Repos_NeitherOrgNorUser_ThrowsAUsefulMessage()
   {
      _gateway.OrgPages = (_, _) => throw NotFound();
      _gateway.UserPages = (_, _) => throw NotFound();

      GitHubAccessException ex = await Assert.ThrowsAsync<GitHubAccessException>(
          () => RepoLister().ListOrganizationRepositoriesAsync("ghost", "tok-1234567890"));

      Assert.Contains("'ghost'", ex.Message);
      Assert.Contains("404", ex.Message);
   }



   [Fact]
   public async Task Repos_BadToken_TranslatesTo401Guidance()
   {
      _gateway.OrgPages = (_, _) => throw Unauthorized();

      GitHubAccessException ex = await Assert.ThrowsAsync<GitHubAccessException>(
          () => RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890"));

      Assert.Contains("401", ex.Message);
      Assert.Contains("PAT", ex.Message);
   }



   [Fact]
   public async Task Repos_RateLimited_TranslatesWithResetTime()
   {
      _gateway.OrgPages = (_, _) => throw RateLimited();

      GitHubAccessException ex = await Assert.ThrowsAsync<GitHubAccessException>(
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

      IReadOnlyList<string> orgs = await OrgLister().ListOrganizationsAsync("tok-1234567890");

      Assert.Equal(["Kof", "acme", "zeta"], orgs);
   }



   [Fact]
   public async Task Orgs_MissingReadOrgScope_DegradesToJustThePersonalAccount()
   {
      _gateway.CurrentUser = () => Task.FromResult("Kof");
      _gateway.OrgLogins = () => throw Forbidden();

      IReadOnlyList<string> orgs = await OrgLister().ListOrganizationsAsync("tok-1234567890");

      Assert.Equal(["Kof"], orgs);
   }



   [Fact]
   public async Task Orgs_SecondaryRateLimit_IsNotMistakenForAMissingScope()
   {
      // A throttle must surface as an error, not silently shrink the org list to
      // the personal account (which reads as a permanent scope problem).
      _gateway.OrgLogins = () => throw SecondaryRateLimited();

      GitHubAccessException ex = await Assert.ThrowsAsync<GitHubAccessException>(
          () => OrgLister().ListOrganizationsAsync("tok-1234567890"));

      Assert.Contains("secondary rate limit", ex.Message);

      _gateway.OrgLogins = () => throw Abuse();
      GitHubAccessException abuse = await Assert.ThrowsAsync<GitHubAccessException>(
          () => OrgLister().ListOrganizationsAsync("tok-1234567890"));
      Assert.Contains("retry in 60 seconds", abuse.Message);
   }



   [Fact]
   public async Task Orgs_LoginAttemptsExceeded_IsNotMistakenForAMissingScope()
   {
      _gateway.OrgLogins = () => throw LoginAttemptsExceeded();

      GitHubAccessException ex = await Assert.ThrowsAsync<GitHubAccessException>(
          () => OrgLister().ListOrganizationsAsync("tok-1234567890"));

      Assert.Contains("too many failed attempts", ex.Message);
   }



   [Fact]
   public async Task Orgs_BadToken_TranslatesTo401Guidance()
   {
      _gateway.CurrentUser = () => throw Unauthorized();

      GitHubAccessException ex = await Assert.ThrowsAsync<GitHubAccessException>(
          () => OrgLister().ListOrganizationsAsync("tok-1234567890"));

      Assert.Contains("401", ex.Message);
   }



   [Fact]
   public async Task Orgs_RateLimited_TranslatesWithResetTime()
   {
      _gateway.OrgLogins = () => throw RateLimited();

      GitHubAccessException ex = await Assert.ThrowsAsync<GitHubAccessException>(
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



   // ---------------------------------------------------------------- failures that are not the request's fault (#153)

   private static ApiException ServerError() => new("Bad Gateway", HttpStatusCode.BadGateway);



   private static HttpRequestException NetworkFailure()
       => new(HttpRequestError.NameResolutionError, "No such host is known. (api.github.com:443)");



   /// <summary>A refusal the way the API sends it: a status and a reason in a JSON document.</summary>
   private static ForbiddenException ForbiddenBecause(string reason)
       => new(new FakeResponse(
           HttpStatusCode.Forbidden,
           body: System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal) { ["message"] = reason })));



   /// <summary>What an HTTP client throws when a request runs into its timeout: a cancellation nobody asked for.</summary>
   private static TaskCanceledException TimedOut() => new("The request was canceled due to the configured HttpClient.Timeout.");



   [Fact]
   public async Task Repos_AccessRefused_IsForbidden_WithGitHubsReason()
   {
      _gateway.OrgPages = (_, _) => throw ForbiddenBecause("Resource protected by organization SAML enforcement");

      GitHubAccessException ex = await Assert.ThrowsAsync<GitHubAccessException>(
          () => RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890"));

      Assert.Equal(GitHubAccessKind.Forbidden, ex.Kind);
      Assert.Equal(
          "GitHub refused access (403): Resource protected by organization SAML enforcement. "
          + "If the organization uses SAML single sign-on, authorize the token for it; "
          + "otherwise check what the token is allowed to access.",
          ex.Message);
      Assert.IsType<ForbiddenException>(ex.InnerException);
   }



   [Fact]
   public async Task Repos_AccessRefused_WhileResolvingAUserAccount_IsForbiddenToo()
   {
      // The refusal can come from any of the requests, not only the first one.
      _gateway.OrgPages = (_, _) => throw NotFound();
      _gateway.AccountKind = _ => throw Forbidden();

      GitHubAccessException ex = await Assert.ThrowsAsync<GitHubAccessException>(
          () => RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890"));

      Assert.Equal(GitHubAccessKind.Forbidden, ex.Kind);
   }



   [Fact]
   public async Task Repos_ErrorOnGitHubsSide_IsUnavailable()
   {
      _gateway.OrgPages = (_, _) => throw ServerError();

      GitHubAccessException ex = await Assert.ThrowsAsync<GitHubAccessException>(
          () => RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890"));

      Assert.Equal(GitHubAccessKind.Unavailable, ex.Kind);
      Assert.Contains("HTTP 502", ex.Message);
      Assert.Contains("retry later", ex.Message);
   }



   [Fact]
   public async Task Repos_NetworkFailure_IsUnavailable_WithTheReason()
   {
      _gateway.OrgPages = (_, _) => throw NetworkFailure();

      GitHubAccessException ex = await Assert.ThrowsAsync<GitHubAccessException>(
          () => RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890"));

      Assert.Equal(GitHubAccessKind.Unavailable, ex.Kind);
      Assert.Equal(
          "GitHub could not be reached: its name could not be resolved (DNS). Check the network connection and retry.",
          ex.Message);
   }



   [Fact]
   public async Task Repos_RequestTimedOut_IsUnavailable_NotACancellation()
   {
      _gateway.OrgPages = (_, _) => throw TimedOut();

      GitHubAccessException ex = await Assert.ThrowsAsync<GitHubAccessException>(
          () => RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890"));

      Assert.Equal(GitHubAccessKind.Unavailable, ex.Kind);
      Assert.Contains("did not answer in time", ex.Message);
   }



   [Fact]
   public async Task Repos_AFailureWithNoKnownMeaning_StaysWhatItIs()
   {
      _gateway.OrgPages = (_, _) => throw new ApiException("Validation Failed", HttpStatusCode.UnprocessableEntity);

      ApiException ex = await Assert.ThrowsAsync<ApiException>(
          () => RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890"));

      Assert.Equal(HttpStatusCode.UnprocessableEntity, ex.StatusCode);
   }



   [Fact]
   public async Task Repos_CancellationDuringARequestThatHangs_DoesNotWaitForIt()
   {
      using var cts = new CancellationTokenSource();
      var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var never = new TaskCompletionSource<IReadOnlyList<GitHubRepo>>(TaskCreationOptions.RunContinuationsAsynchronously);
      _gateway.OrgPages = (_, _) =>
      {
         requested.SetResult();
         return never.Task;
      };

      Task<IReadOnlyList<RepoDescriptor>> listing = RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890", cts.Token);
      await requested.Task.WaitAsync(TimeSpan.FromSeconds(30));
      cts.Cancel();

      OperationCanceledException ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
          () => listing.WaitAsync(TimeSpan.FromSeconds(30)));
      Assert.Equal(cts.Token, ex.CancellationToken); // the caller's cancellation, not a timeout
   }



   [Fact]
   public async Task Repos_CancellationWhileTheAccountKindIsLookedUp_DoesNotWaitForIt()
   {
      using var cts = new CancellationTokenSource();
      var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var never = new TaskCompletionSource<GitHubAccountKind>(TaskCreationOptions.RunContinuationsAsynchronously);
      _gateway.OrgPages = (_, _) => throw NotFound();
      _gateway.AccountKind = _ =>
      {
         requested.SetResult();
         return never.Task;
      };

      Task<IReadOnlyList<RepoDescriptor>> listing = RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890", cts.Token);
      await requested.Task.WaitAsync(TimeSpan.FromSeconds(30));
      cts.Cancel();

      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listing.WaitAsync(TimeSpan.FromSeconds(30)));
   }



   [Fact]
   public async Task Orgs_AccessRefusedForTheAccountItself_IsForbidden()
   {
      // Only a refusal of the organization list is the missing-scope case; this one is not.
      _gateway.CurrentUser = () => throw Forbidden();

      GitHubAccessException ex = await Assert.ThrowsAsync<GitHubAccessException>(
          () => OrgLister().ListOrganizationsAsync("tok-1234567890"));

      Assert.Equal(GitHubAccessKind.Forbidden, ex.Kind);
   }



   [Fact]
   public async Task Orgs_ErrorOnGitHubsSide_NetworkFailure_AndTimeout_AreUnavailable()
   {
      _gateway.OrgLogins = () => throw ServerError();
      GitHubAccessException server = await Assert.ThrowsAsync<GitHubAccessException>(
          () => OrgLister().ListOrganizationsAsync("tok-1234567890"));

      _gateway.OrgLogins = () => throw NetworkFailure();
      GitHubAccessException network = await Assert.ThrowsAsync<GitHubAccessException>(
          () => OrgLister().ListOrganizationsAsync("tok-1234567890"));

      _gateway.OrgLogins = () => throw TimedOut();
      GitHubAccessException timeout = await Assert.ThrowsAsync<GitHubAccessException>(
          () => OrgLister().ListOrganizationsAsync("tok-1234567890"));

      Assert.Equal(GitHubAccessKind.Unavailable, server.Kind);
      Assert.Equal(GitHubAccessKind.Unavailable, network.Kind);
      Assert.Equal(GitHubAccessKind.Unavailable, timeout.Kind);
   }



   [Fact]
   public async Task Orgs_CancellationDuringARequestThatHangs_DoesNotWaitForIt()
   {
      using var cts = new CancellationTokenSource();
      var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var never = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
      _gateway.OrgLogins = () =>
      {
         requested.SetResult();
         return never.Task;
      };

      Task<IReadOnlyList<string>> listing = OrgLister().ListOrganizationsAsync("tok-1234567890", cts.Token);
      await requested.Task.WaitAsync(TimeSpan.FromSeconds(30));
      cts.Cancel();

      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listing.WaitAsync(TimeSpan.FromSeconds(30)));
   }



   // ---------------------------------------------------------------- the shared mapping itself (#153)

   [Fact]
   public void Failures_EveryKnownFailure_HasItsKind()
   {
      (Exception Failure, GitHubAccessKind Kind)[] known =
      [
          (Unauthorized(), GitHubAccessKind.Unauthorized),
          (RateLimited(), GitHubAccessKind.RateLimited),
          (SecondaryRateLimited(), GitHubAccessKind.RateLimited),
          (Abuse(), GitHubAccessKind.RateLimited),
          (LoginAttemptsExceeded(), GitHubAccessKind.RateLimited),
          (Forbidden(), GitHubAccessKind.Forbidden),
          (new LegalRestrictionException("Repository access blocked", (HttpStatusCode)451), GitHubAccessKind.Forbidden),
          (new ApiException("Too Many Requests", HttpStatusCode.TooManyRequests), GitHubAccessKind.RateLimited),
          (new ApiException("Proxy Authentication Required", HttpStatusCode.ProxyAuthenticationRequired), GitHubAccessKind.Unavailable),
          (ServerError(), GitHubAccessKind.Unavailable),
          (new ApiException("Service Unavailable", HttpStatusCode.ServiceUnavailable), GitHubAccessKind.Unavailable),
          (NetworkFailure(), GitHubAccessKind.Unavailable),
          (TimedOut(), GitHubAccessKind.Unavailable),
      ];

      Assert.All(known, k =>
      {
         Assert.True(GitHubFailures.Knows(k.Failure, CancellationToken.None), k.Failure.GetType().Name);
         GitHubAccessException translated = GitHubFailures.Translate(k.Failure);
         Assert.Equal(k.Kind, translated.Kind);
         Assert.Same(k.Failure, translated.InnerException);
      });
   }



   [Fact]
   public void Failures_ALegalRestriction_SaysItsStatusAndReason()
   {
      var blocked = new LegalRestrictionException(new FakeResponse(
          (HttpStatusCode)451, body: "{\"message\":\"Repository access blocked\"}"));

      GitHubAccessException translated = GitHubFailures.Translate(blocked);

      Assert.StartsWith("GitHub refused access (451): Repository access blocked. If the organization", translated.Message);
   }



   [Fact]
   public void Failures_WhatHasNoKnownMeaning_IsNotTranslated()
   {
      using var canceled = new CancellationTokenSource();
      canceled.Cancel();
      Exception[] unknown =
      [
          NotFound(), // each lister decides what "not found" means where it happens
          new ApiException("Validation Failed", HttpStatusCode.UnprocessableEntity),
          new InvalidOperationException("a bug"),
          new GitHubAccessException(GitHubAccessKind.Forbidden, "already translated"),
      ];

      Assert.All(unknown, failure => Assert.False(GitHubFailures.Knows(failure, CancellationToken.None), failure.GetType().Name));
      // A cancellation the caller asked for is a cancellation, not a timeout.
      Assert.False(GitHubFailures.Knows(new OperationCanceledException(canceled.Token), canceled.Token));
      Assert.Throws<ArgumentException>(() => GitHubFailures.Translate(new InvalidOperationException("a bug")));
   }



   [Fact]
   public void Failures_ACancellationThatArrivesAfterTheDecision_DoesNotChangeIt()
   {
      // The request timed out while the caller had not canceled: that is decided once.
      using var caller = new CancellationTokenSource();
      TaskCanceledException timeout = TimedOut();

      bool known = GitHubFailures.Knows(timeout, caller.Token);
      caller.Cancel(); // lands between the decision and the translation
      GitHubAccessException translated = GitHubFailures.Translate(timeout);

      Assert.True(known);
      Assert.Equal(GitHubAccessKind.Unavailable, translated.Kind);
   }



   [Fact]
   public async Task Repos_AFailureThatArrivesWhenTheRequestEnds_IsTranslatedLikeOneThrownAtOnce()
   {
      // The real gateway fails the request it returned; it does not throw before returning one.
      _gateway.OrgPages = (_, _) => Task.FromException<IReadOnlyList<GitHubRepo>>(ServerError());

      GitHubAccessException ex = await Assert.ThrowsAsync<GitHubAccessException>(
          () => RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890"));

      Assert.Equal(GitHubAccessKind.Unavailable, ex.Kind);
   }



   [Fact]
   public async Task Repos_ARequestAbandonedOnCancellation_MayFailLater_WithoutAnyoneBeingTold()
   {
      using var cts = new CancellationTokenSource();
      var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var request = new TaskCompletionSource<IReadOnlyList<GitHubRepo>>(TaskCreationOptions.RunContinuationsAsynchronously);
      _gateway.OrgPages = (_, _) =>
      {
         requested.SetResult();
         return request.Task;
      };
      Task<IReadOnlyList<RepoDescriptor>> listing = RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890", cts.Token);
      await requested.Task.WaitAsync(TimeSpan.FromSeconds(30));
      cts.Cancel();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listing.WaitAsync(TimeSpan.FromSeconds(30)));

      request.SetException(NetworkFailure()); // the abandoned request ends on its own, badly

      // The listing ended as a cancellation and stays one; the late failure belongs to nobody.
      Assert.True(listing.IsCanceled);
      await Assert.ThrowsAsync<HttpRequestException>(() => request.Task);
   }



   [Fact]
   public async Task Repos_TooManyRequests_IsARateLimit_AlsoAsStatus429()
   {
      _gateway.OrgPages = (_, _) => throw new ApiException("Too Many Requests", HttpStatusCode.TooManyRequests);

      GitHubAccessException ex = await Assert.ThrowsAsync<GitHubAccessException>(
          () => RepoLister().ListOrganizationRepositoriesAsync("acme", "tok-1234567890"));

      Assert.Equal(GitHubAccessKind.RateLimited, ex.Kind);
      Assert.Contains("429", ex.Message);
   }



   [Theory]
   [InlineData(HttpRequestError.NameResolutionError, "its name could not be resolved (DNS)")]
   [InlineData(HttpRequestError.ConnectionError, "the connection could not be made")]
   [InlineData(HttpRequestError.SecureConnectionError, "a secure (TLS) connection could not be established")]
   [InlineData(HttpRequestError.ProxyTunnelError, "the proxy on the way refused the connection")]
   [InlineData(HttpRequestError.Unknown, "the request failed on the way.")]
   [InlineData(HttpRequestError.ResponseEnded, "the request failed on the way (ResponseEnded)")]
   public void Failures_ANetworkFailure_IsDescribedByItsKind(HttpRequestError error, string expected)
   {
      GitHubAccessException translated = GitHubFailures.Translate(new HttpRequestException(error, "whatever the platform says"));

      Assert.StartsWith("GitHub could not be reached: ", translated.Message);
      Assert.Contains(expected, translated.Message);
      Assert.EndsWith(" Check the network connection and retry.", translated.Message);
   }



   [Fact]
   public void Failures_ANetworkFailure_NeverCopiesThePlatformsText_WhichCanNameAProxyWithItsPassword()
   {
      var failure = new HttpRequestException(
          HttpRequestError.ProxyTunnelError,
          "The proxy tunnel request to proxy 'http://proxyuser:PROXYPASSWORD@10.0.0.1:8080/' failed with status code '407'.");

      GitHubAccessException translated = GitHubFailures.Translate(failure);

      Assert.DoesNotContain("PROXYPASSWORD", translated.Message);
      Assert.DoesNotContain("proxyuser", translated.Message);
      Assert.Same(failure, translated.InnerException); // the full text stays with the cause, for the log
   }



   [Fact]
   public void Failures_ARefusalWithoutAReasonFromTheApi_SaysNone_AndCopiesNoPage()
   {
      // A web filter or a proxy answers 403 with a page of its own; that is not GitHub's reason.
      var blockPage = new ForbiddenException(new FakeResponse(
          HttpStatusCode.Forbidden, body: "<html><body><h1>Blocked by policy</h1>" + new string('x', 3000) + "</body></html>"));

      GitHubAccessException translated = GitHubFailures.Translate(blockPage);

      Assert.Equal(
          "GitHub refused access (403). If the organization uses SAML single sign-on, authorize the token for it; "
          + "otherwise check what the token is allowed to access.",
          translated.Message);
   }



   [Theory]
   [InlineData("")]
   [InlineData("[]")]
   [InlineData("{\"other\":1}")]
   [InlineData("{\"message\":42}")]
   [InlineData("{\"message\":\"   \"}")]
   public void Failures_ARefusalWhoseAnswerHasNoReasonInIt_SaysNone(string body)
   {
      GitHubAccessException translated = GitHubFailures.Translate(
          new ForbiddenException(new FakeResponse(HttpStatusCode.Forbidden, body: body)));

      Assert.StartsWith("GitHub refused access (403). If the organization", translated.Message);
   }



   [Fact]
   public void Failures_AReasonFromTheApi_IsPutOnOneLine_AndCut()
   {
      string emoji = char.ConvertFromUtf32(0x1F600);
      GitHubAccessException lines = GitHubFailures.Translate(ForbiddenBecause("  first line\r\n   second\tline?  "));
      GitHubAccessException longReason = GitHubFailures.Translate(ForbiddenBecause(new string('r', 400)));
      GitHubAccessException cutAtAPair = GitHubFailures.Translate(ForbiddenBecause(new string('r', 299) + emoji + "tail"));

      Assert.StartsWith("GitHub refused access (403): first line second line? If the organization", lines.Message);
      Assert.StartsWith("GitHub refused access (403): " + new string('r', 300) + "... If the organization", longReason.Message);
      Assert.StartsWith("GitHub refused access (403): " + new string('r', 299) + "... If the organization", cutAtAPair.Message);
   }
}
