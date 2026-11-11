/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using static gclo.Engine.Tests.FakeGiteaServer;


namespace gclo.Engine.Tests;


/// <summary>
/// The Gitea listers over a fake server: paging, the account fallback, what is and is
/// not taken from the server's answers, and what every kind of failure means. The
/// server's address is one a user typed, so the tests also pin where the token goes
/// and that nothing the server says moves it elsewhere.
/// </summary>
public sealed class GiteaListerTests : IDisposable
{
   private const string Token = "gitea-test-token";

   private const string Host = "git.example.test";

   private readonly FakeGiteaServer _server = new();



   public void Dispose() => _server.Dispose();



   private GiteaRepositoryLister Repos(TimeSpan? requestTimeout = null) => new(_server.Api(requestTimeout));



   private GiteaOrganizationLister Orgs() => new(_server.Api());



   private static string Account(string login) => $$"""{"id":1,"login":"{{login}}","username":"","email":"someone@example.test"}""";



   private void TheTokenBelongsTo(string login) => _server.On("user", () => Json(Account(login)));



   private Task<IReadOnlyList<RepoDescriptor>> List(string organization = "acme", CancellationToken cancellationToken = default)
       => Repos().ListOrganizationRepositoriesAsync(organization, Token, cancellationToken);



   /// <summary>A body that never ends: a server that keeps sending.</summary>
   private sealed class Endless : Stream
   {
      public override bool CanRead => true;

      public override bool CanSeek => false;

      public override bool CanWrite => false;

      public override long Length => throw new NotSupportedException();

      public override long Position
      {
         get => throw new NotSupportedException();
         set => throw new NotSupportedException();
      }



      public override void Flush()
      {
      }



      public override int Read(byte[] buffer, int offset, int count)
      {
         Array.Fill(buffer, (byte)' ', offset, count);
         return count;
      }



      public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();



      public override void SetLength(long value) => throw new NotSupportedException();



      public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
   }



   // ---------------------------------------------------------------- paging

   [Fact]
   public async Task Repos_PagesWhileTheServerSaysThereIsANextPage_AndSortsByName()
   {
      // The second page is empty (everything on it was hidden from the token) and
      // the first is not full: neither ends a list the server says goes on.
      _server
          .OnPage("orgs/acme/repos", 1, [Repo("zeta"), Repo("Alpha")], more: true)
          .OnPage("orgs/acme/repos", 2, [], more: true)
          .OnPage("orgs/acme/repos", 3, [Repo("mid")], more: false);

      IReadOnlyList<RepoDescriptor> repos = await List();

      Assert.Equal(["Alpha", "mid", "zeta"], repos.Select(r => r.Name), StringComparer.Ordinal);
      Assert.Equal(
          ["orgs/acme/repos?page=1&limit=50", "orgs/acme/repos?page=2&limit=50", "orgs/acme/repos?page=3&limit=50"],
          _server.Asked,
          StringComparer.Ordinal);
   }



   [Fact]
   public async Task Repos_WhereTheServerDoesNotSay_AnEmptyPageEndsTheList()
   {
      _server
          .OnPage("orgs/acme/repos", 1, [Repo("a")])
          .OnPage("orgs/acme/repos", 2, [Repo("b")])
          .OnPage("orgs/acme/repos", 3, []);

      IReadOnlyList<RepoDescriptor> repos = await List();

      Assert.Equal(["a", "b"], repos.Select(r => r.Name), StringComparer.Ordinal);
      Assert.Equal(3, _server.Asked.Count);
   }



   [Fact]
   public async Task Repos_AListThatDoesNotEnd_IsNotFollowedForEver()
   {
      for(int page = 1; page <= GiteaApi.MaxPages; page++)
      {
         _server.OnPage("orgs/acme/repos", page, [Repo("same")], more: true);
      }

      ForgeAccessException ex = await Assert.ThrowsAsync<ForgeAccessException>(() => List());

      Assert.Equal(ForgeAccessKind.NotFound, ex.Kind);
      Assert.Contains("did not answer like a Gitea server", ex.Message);
      Assert.Equal(GiteaApi.MaxPages, _server.Asked.Count);
   }



   [Fact]
   public async Task Repos_DuplicateNames_AreListedOnce()
   {
      _server.OnPage("orgs/acme/repos", 1, [Repo("Tool"), Repo("tool"), Repo("other")], more: false);

      IReadOnlyList<RepoDescriptor> repos = await List();

      Assert.Equal(["other", "Tool"], repos.Select(r => r.Name), StringComparer.Ordinal);
   }



   // ---------------------------------------------------------------- what is taken from the answers

   [Fact]
   public async Task Repos_MapFields_AndTheCloneAddressIsBuiltFromTheAccountsServer_NotTakenFromTheAnswer()
   {
      _server.OnPage(
          "orgs/acme/repos",
          1,
          [Repo("tool", archived: true), Repo("fresh", empty: true), Repo("odd", branch: ""), Repo("bare", branch: null), """{"name":" "}""", "null"],
          more: false);

      IReadOnlyList<RepoDescriptor> repos = await List();

      Assert.Equal(
          [
             new RepoDescriptor("bare", $"https://{Host}/acme/bare.git", null, false),
             new RepoDescriptor("fresh", $"https://{Host}/acme/fresh.git", null, false), // no commits: no branch to speak of
             new RepoDescriptor("odd", $"https://{Host}/acme/odd.git", null, false),
             new RepoDescriptor("tool", $"https://{Host}/acme/tool.git", "main", true),
          ],
          repos);
      Assert.DoesNotContain(repos, r => r.CloneUrl.Contains("attacker", StringComparison.Ordinal));
   }



   [Fact]
   public async Task Repos_AServerUnderAPath_IsAskedThere_AndClonedFromThere()
   {
      var server = new FakeGiteaServer();
      server.On("/gitea/api/v1/orgs/acme/repos?page=1&limit=50", () => Json($"[{Repo("tool")}]", link: false));
      var lister = new GiteaRepositoryLister(new GiteaApi(new Uri("https://example.test/gitea"), new HttpClient(server)));

      IReadOnlyList<RepoDescriptor> repos = await lister.ListOrganizationRepositoriesAsync("acme", Token);

      Assert.Equal("https://example.test/gitea/acme/tool.git", Assert.Single(repos).CloneUrl);
   }



   [Fact]
   public async Task EveryRequest_GoesToTheAccountsServer_WithTheTokenInItsHeader_WhateverTheAnswersPointAt()
   {
      // The Link headers and the clone addresses of the answers name other hosts.
      _server
          .OnPage("orgs/acme/repos", 1, [Repo("a")], more: true)
          .OnPage("orgs/acme/repos", 2, [Repo("b")], more: false);

      await List();

      Assert.Equal(2, _server.Requests.Count);
      Assert.All(_server.Requests, request =>
      {
         Assert.Equal(Uri.UriSchemeHttps, request.Address.Scheme);
         Assert.Equal(Host, request.Address.Host);
         Assert.Equal("token " + Token, request.Authorization);
         Assert.DoesNotContain(Token, request.Address.AbsoluteUri, StringComparison.Ordinal);
      });
   }



   // ---------------------------------------------------------------- an account that is not an organization

   [Fact]
   public async Task Repos_OfTheTokensOwnAccount_AreItsOwn_NotThoseOfOthersItMayWorkOn()
   {
      TheTokenBelongsTo("Me");
      _server.OnPage(
          "user/repos",
          1,
          [Repo("mine", owner: "Me"), Repo("theirs", owner: "acme"), Repo("ownerless", owner: null)],
          more: false);

      IReadOnlyList<RepoDescriptor> repos = await List("me");

      Assert.Equal(["mine", "ownerless"], repos.Select(r => r.Name), StringComparer.Ordinal);
      Assert.Equal($"https://{Host}/Me/mine.git", repos[0].CloneUrl);
      Assert.Equal($"https://{Host}/me/ownerless.git", repos[1].CloneUrl); // no owner in the answer: the name that was asked for
      Assert.Equal(["orgs/me/repos?page=1&limit=50", "user", "user/repos?page=1&limit=50"], _server.Asked, StringComparer.Ordinal);
   }



   [Fact]
   public async Task Repos_OfAnotherAccount_AreWhatTheTokenSeesThere()
   {
      TheTokenBelongsTo("me");
      _server.OnPage("users/bob/repos", 1, [Repo("b", owner: "bob")], more: false);

      IReadOnlyList<RepoDescriptor> repos = await List("bob");

      Assert.Equal($"https://{Host}/bob/b.git", Assert.Single(repos).CloneUrl);
   }



   [Fact]
   public async Task Repos_NeitherOrganizationNorAccount_IsNotFound_WithASpellingHint()
   {
      TheTokenBelongsTo("me");

      ForgeAccessException ex = await Assert.ThrowsAsync<ForgeAccessException>(() => List("nobody"));

      Assert.Equal(ForgeAccessKind.NotFound, ex.Kind);
      Assert.Contains($"'nobody' was found neither as an organization nor as a user account on {Host} (404)", ex.Message);
      Assert.Contains("Check the spelling", ex.Message);
   }



   [Fact]
   public async Task Repos_WhenTheServerNamesNoAccountForTheToken_ItDoesNotAnswerLikeAGiteaServer()
   {
      _server.On("user", () => Json("""{"id":1}"""));

      ForgeAccessException ex = await Assert.ThrowsAsync<ForgeAccessException>(() => List());

      Assert.Equal(ForgeAccessKind.NotFound, ex.Kind);
      Assert.Contains("did not answer like a Gitea server", ex.Message);
   }



   // ---------------------------------------------------------------- arguments and cancellation

   [Theory]
   [InlineData("../escape")]
   [InlineData("nested/child")]
   [InlineData("name:stream")]
   public async Task Repos_ANameThatIsNotOnePlainName_IsRefusedBeforeAnyRequest(string organization)
   {
      ForgeAccessException ex = await Assert.ThrowsAsync<ForgeAccessException>(() => List(organization));

      Assert.Equal(ForgeAccessKind.NotFound, ex.Kind);
      Assert.Contains("is not an organization or account name", ex.Message);
      Assert.Empty(_server.Requests);
   }



   [Theory]
   [InlineData("", Token)]
   [InlineData("   ", Token)]
   [InlineData("acme", "")]
   [InlineData("acme", "   ")]
   public async Task Repos_BlankArguments_Throw(string organization, string token)
   {
      await Assert.ThrowsAnyAsync<ArgumentException>(() => Repos().ListOrganizationRepositoriesAsync(organization, token));
      Assert.Empty(_server.Requests);
   }



   [Fact]
   public async Task Repos_AlreadyCanceled_ThrowBeforeAnyRequest()
   {
      using var cts = new CancellationTokenSource();
      cts.Cancel();

      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => List(cancellationToken: cts.Token));
      Assert.Empty(_server.Requests);
   }



   [Fact]
   public async Task Repos_CanceledBetweenPages_Throw_AndAskForNoMore()
   {
      using var cts = new CancellationTokenSource();
      _server
          .On("orgs/acme/repos?page=1&limit=50", () =>
          {
             cts.Cancel(); // while the first page is on its way
             return Json($"[{Repo("a")}]", link: true);
          })
          .OnPage("orgs/acme/repos", 2, [Repo("b")], more: false);

      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => List(cancellationToken: cts.Token));
      Assert.Single(_server.Asked);
   }



   [Fact]
   public async Task Repos_CanceledDuringARequestThatHangs_IsACancellation_NotAFailureOfTheServer()
   {
      using var cts = new CancellationTokenSource();
      _server.OnRequest = async (_, token) =>
      {
         cts.CancelAfter(TimeSpan.FromMilliseconds(20));
         await Task.Delay(Timeout.Infinite, token);
      };

      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => List(cancellationToken: cts.Token));
   }



   // ---------------------------------------------------------------- failures

   [Theory]
   [InlineData(HttpStatusCode.Unauthorized, ForgeAccessKind.Unauthorized, "rejected the token (401)")]
   [InlineData(HttpStatusCode.TooManyRequests, ForgeAccessKind.RateLimited, "limiting requests from this token (429")]
   [InlineData(HttpStatusCode.ProxyAuthenticationRequired, ForgeAccessKind.Unavailable, "A proxy on the way to git.example.test asked gclo to sign in (HTTP 407)")]
   [InlineData(HttpStatusCode.InternalServerError, ForgeAccessKind.Unavailable, "answered with an error on its side (HTTP 500); retry later")]
   [InlineData(HttpStatusCode.BadGateway, ForgeAccessKind.Unavailable, "answered with an error on its side (HTTP 502); retry later")]
   [InlineData(HttpStatusCode.Found, ForgeAccessKind.NotFound, "answered with a redirect (HTTP 302) instead of the Gitea API")]
   [InlineData(HttpStatusCode.PermanentRedirect, ForgeAccessKind.NotFound, "answered with a redirect (HTTP 308) instead of the Gitea API")]
   [InlineData(HttpStatusCode.BadRequest, ForgeAccessKind.NotFound, "There is no Gitea API at this address of git.example.test (HTTP 400)")]
   [InlineData(HttpStatusCode.MethodNotAllowed, ForgeAccessKind.NotFound, "There is no Gitea API at this address of git.example.test (HTTP 405)")]
   public async Task AStatusThatIsNotSuccess_HasItsKind_AndNamesTheServer(HttpStatusCode status, ForgeAccessKind kind, string said)
   {
      _server.On("orgs/acme/repos?page=1&limit=50", () => Refusal(status, "whatever the server says"));

      ForgeAccessException ex = await Assert.ThrowsAsync<ForgeAccessException>(() => List());

      Assert.Equal(kind, ex.Kind);
      Assert.Contains(said, ex.Message);
      Assert.Single(_server.Requests); // nothing is asked a second time, and a redirect is not followed
      Assert.IsType<GiteaStatusException>(ex.InnerException);
   }



   [Fact]
   public async Task AnAddressWithNoApiBehindIt_SaysSo_InsteadOfBlamingTheNameThatWasAskedFor()
   {
      // Every path is 404, the 'who am I' of the account fallback too.
      ForgeAccessException ex = await Assert.ThrowsAsync<ForgeAccessException>(() => List());

      Assert.Equal(ForgeAccessKind.NotFound, ex.Kind);
      Assert.Contains($"There is no Gitea API at this address of {Host} (HTTP 404). Check the server address of this account.", ex.Message);
   }



   [Fact]
   public async Task ARefusal_CarriesTheServersReason_OnOneLine_AndSaysWhichScopesAreNeeded()
   {
      _server.On(
          "orgs/acme/repos?page=1&limit=50",
          () => Refusal(HttpStatusCode.Forbidden, "token does not have at least one of required scope(s):\\n [read:organization]"));

      ForgeAccessException ex = await Assert.ThrowsAsync<ForgeAccessException>(() => List());

      Assert.Equal(ForgeAccessKind.Forbidden, ex.Kind);
      Assert.Equal(
          $"{Host} refused access (403): token does not have at least one of required scope(s): [read:organization]. "
          + "Check what the access token is allowed to do: gclo reads repositories, organizations and the user "
          + "(the scopes read:repository, read:organization and read:user).",
          ex.Message);
   }



   [Theory]
   [InlineData("<html><body>Blocked by the web filter</body></html>")]
   [InlineData("""{"error":"no message here"}""")]
   [InlineData("")]
   public async Task ARefusalWithoutAReasonFromTheApi_SaysNone_AndCopiesNoPage(string body)
   {
      _server.On("orgs/acme/repos?page=1&limit=50", () => Json(body, HttpStatusCode.Forbidden));

      ForgeAccessException ex = await Assert.ThrowsAsync<ForgeAccessException>(() => List());

      Assert.StartsWith($"{Host} refused access (403). Check what the access token is allowed to do", ex.Message, StringComparison.Ordinal);
      Assert.DoesNotContain("web filter", ex.Message, StringComparison.Ordinal);
   }



   [Fact]
   public async Task ARefusalThatDoesNotEnd_IsNotReadForItsReason()
   {
      _server.On(
          "orgs/acme/repos?page=1&limit=50",
          () => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StreamContent(new Endless()) });

      ForgeAccessException ex = await Assert.ThrowsAsync<ForgeAccessException>(() => List());

      Assert.StartsWith($"{Host} refused access (403). ", ex.Message, StringComparison.Ordinal);
   }



   [Theory]
   [InlineData("<html><body>Sign in</body></html>")] // a sign-in page in front of the server
   [InlineData("null")]
   [InlineData("""{"not":"a list"}""")]
   [InlineData("""[{"name":5}]""")]
   public async Task AnAnswerThatIsNotTheApisJson_SaysThatTheAddressIsNotAGiteaServers(string body)
   {
      _server.On("orgs/acme/repos?page=1&limit=50", () => Json(body));

      ForgeAccessException ex = await Assert.ThrowsAsync<ForgeAccessException>(() => List());

      Assert.Equal(ForgeAccessKind.NotFound, ex.Kind);
      Assert.Contains($"{Host} did not answer like a Gitea server. Check the server address of this account.", ex.Message);
      Assert.DoesNotContain("Sign in", ex.Message, StringComparison.Ordinal);
   }



   [Fact]
   public async Task AnAnswerThatDoesNotEnd_IsNotReadForEver()
   {
      _server.On(
          "orgs/acme/repos?page=1&limit=50",
          () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Endless()) });

      ForgeAccessException ex = await Assert.ThrowsAsync<ForgeAccessException>(() => List());

      Assert.Equal(ForgeAccessKind.NotFound, ex.Kind);
      Assert.Contains("did not answer like a Gitea server", ex.Message);
   }



   [Fact]
   public async Task AServerThatCannotBeReached_IsUnavailable_InWordsThatAreNotThePlatformsOwn()
   {
      _server.OnRequest = (_, _) => throw new HttpRequestException(
          HttpRequestError.NameResolutionError, "proxy http://user:hunter2@proxy.example.test said no");

      ForgeAccessException ex = await Assert.ThrowsAsync<ForgeAccessException>(() => List());

      Assert.Equal(ForgeAccessKind.Unavailable, ex.Kind);
      Assert.Equal($"{Host} could not be reached: its name could not be resolved (DNS). Check the network connection and retry.", ex.Message);
   }



   [Fact]
   public async Task ARequestThatTakesTooLong_IsUnavailable_NotACancellation()
   {
      _server.OnRequest = (_, token) => Task.Delay(Timeout.Infinite, token);

      ForgeAccessException ex = await Assert.ThrowsAsync<ForgeAccessException>(
          () => Repos(requestTimeout: TimeSpan.FromMilliseconds(30)).ListOrganizationRepositoriesAsync("acme", Token));

      Assert.Equal(ForgeAccessKind.Unavailable, ex.Kind);
      Assert.Equal($"{Host} did not answer in time; retry later.", ex.Message);
   }



   [Fact]
   public async Task AFailureWithNoKnownMeaning_StaysWhatItIs()
   {
      _server.OnRequest = (_, _) => throw new InvalidOperationException("something nobody planned for");

      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => List());

      Assert.IsNotType<ForgeAccessException>(ex);
      Assert.Equal("something nobody planned for", ex.Message);
   }



   [Fact]
   public void Failures_WhatHasNoKnownMeaning_IsNotTranslated_AndACancellationByTheCallerIsNotATimeout()
   {
      GiteaApi api = _server.Api();
      using var canceled = new CancellationTokenSource();
      canceled.Cancel();

      Assert.False(api.Knows(new InvalidOperationException("?"), CancellationToken.None));
      Assert.Throws<ArgumentException>(() => api.Translate(new InvalidOperationException("?")));
      Assert.True(api.Knows(new OperationCanceledException(), CancellationToken.None));
      Assert.False(api.Knows(new OperationCanceledException(), canceled.Token));

      // Decided when it was asked: a cancellation that arrives afterwards changes nothing.
      Assert.Equal(ForgeAccessKind.Unavailable, api.Translate(new OperationCanceledException()).Kind);
   }



   // ---------------------------------------------------------------- organizations

   [Fact]
   public async Task Orgs_TheTokensOwnAccountFirst_ThenItsOrganizationsSorted_EachOnce()
   {
      TheTokenBelongsTo("me");
      _server.OnPage(
          "user/orgs",
          1,
          ["""{"id":2,"username":"zeta"}""", """{"id":3,"username":"Alpha","login":""}""", """{"id":4,"username":"ME"}""", "null", """{"id":5}""", """{"id":6,"username":"alpha"}"""],
          more: false);

      IReadOnlyList<string> names = await Orgs().ListOrganizationsAsync(Token);

      Assert.Equal(["me", "Alpha", "zeta"], names, StringComparer.Ordinal);
      Assert.All(_server.Requests, request => Assert.Equal("token " + Token, request.Authorization));
   }



   [Fact]
   public async Task Orgs_ATokenThatMayNotListOrganizations_StillGetsItsOwnAccount()
   {
      TheTokenBelongsTo("me");
      _server.On(
          "user/orgs?page=1&limit=50",
          () => Refusal(HttpStatusCode.Forbidden, "token does not have at least one of required scope(s): [read:organization]"));

      IReadOnlyList<string> names = await Orgs().ListOrganizationsAsync(Token);

      Assert.Equal(["me"], names, StringComparer.Ordinal);
   }



   [Fact]
   public async Task Orgs_ATokenThatMayNotReadItsOwnAccount_IsForbidden_WithTheServersReason()
   {
      _server.On("user", () => Refusal(HttpStatusCode.Forbidden, "token does not have at least one of required scope(s): [read:user]"));

      ForgeAccessException ex = await Assert.ThrowsAsync<ForgeAccessException>(() => Orgs().ListOrganizationsAsync(Token));

      Assert.Equal(ForgeAccessKind.Forbidden, ex.Kind);
      Assert.Contains("[read:user]", ex.Message);
   }



   [Fact]
   public async Task Orgs_ATokenTheServerRejects_IsUnauthorized()
   {
      _server.On("user", () => Refusal(HttpStatusCode.Unauthorized, "invalid username, password or token"));

      ForgeAccessException ex = await Assert.ThrowsAsync<ForgeAccessException>(() => Orgs().ListOrganizationsAsync(Token));

      Assert.Equal(ForgeAccessKind.Unauthorized, ex.Kind);
      Assert.Equal($"{Host} rejected the token (401). Check that the access token is complete and has not been deleted or expired.", ex.Message);
   }



   [Fact]
   public async Task Orgs_AlreadyCanceled_OrABlankToken_AskNothing()
   {
      using var cts = new CancellationTokenSource();
      cts.Cancel();

      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Orgs().ListOrganizationsAsync(Token, cts.Token));
      await Assert.ThrowsAnyAsync<ArgumentException>(() => Orgs().ListOrganizationsAsync(" "));
      Assert.Empty(_server.Requests);
   }



   [Fact]
   public async Task Orgs_CanceledAfterTheAccountWasRead_Throw()
   {
      using var cts = new CancellationTokenSource();
      TheTokenBelongsTo("me");
      _server.On("user/orgs?page=1&limit=50", () =>
      {
         cts.Cancel(); // while the answer is on its way
         return Json("[]", link: false);
      });

      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Orgs().ListOrganizationsAsync(Token, cts.Token));
   }



   // ---------------------------------------------------------------- construction

   [Fact]
   public void Listers_AreMadeForAnHttpsAddress_AndForNoOther()
   {
      _ = new GiteaRepositoryLister(new Uri("https://git.example.test"));
      _ = new GiteaOrganizationLister(new Uri("https://git.example.test/gitea/"));

      ArgumentException plain = Assert.Throws<ArgumentException>(() => new GiteaRepositoryLister(new Uri("http://git.example.test")));
      Assert.Contains("it has to begin with https://", plain.Message);
      Assert.Throws<ArgumentException>(() => new GiteaOrganizationLister(new Uri("https://user:secret@git.example.test")));
      Assert.Throws<ArgumentNullException>(() => new GiteaRepositoryLister((Uri)null!));
      Assert.Throws<ArgumentNullException>(() => new GiteaRepositoryLister((GiteaApi)null!));
      Assert.Throws<ArgumentNullException>(() => new GiteaOrganizationLister((GiteaApi)null!));
   }



   [Fact]
   public void InProduction_RedirectsAreNotFollowed_AndCertificatesAreJudgedByThePlatformAlone()
   {
      // A redirect would carry the request, and with it the token, to another host;
      // a validation callback is how certificate checks are switched off.
      Assert.False(GiteaApi.SharedHandler.AllowAutoRedirect);
      Assert.Null(GiteaApi.SharedHandler.SslOptions.RemoteCertificateValidationCallback);
   }



   [Fact]
   public void TheServer_IsNamedInMessages_ByItsHost_AndItsPortWhenThatIsNotTheUsual()
   {
      Assert.Equal("git.example.test", new GiteaApi(new Uri("https://git.example.test/gitea")).Host);
      Assert.Equal("git.example.test:8443", new GiteaApi(new Uri("https://git.example.test:8443")).Host);
      Assert.Equal("https://git.example.test/gitea/", new GiteaApi(new Uri("https://GIT.example.test/gitea")).Server.AbsoluteUri);
   }
}
