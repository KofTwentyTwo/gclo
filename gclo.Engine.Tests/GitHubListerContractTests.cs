/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using Octokit;


namespace gclo.Engine.Tests;


/// <summary>The lister contract, kept by the GitHub listers.</summary>
public sealed class GitHubListerContractTests : ListerContractTests
{
   private readonly GitHubListerTests.FakeGateway _gateway = new();

   private int _requests;



   /// <inheritdoc/>
   protected override IRepositoryLister Repositories => new GitHubRepositoryLister(_ => _gateway);

   /// <inheritdoc/>
   protected override IOrganizationLister Organizations => new GitHubOrganizationLister(_ => _gateway);

   /// <inheritdoc/>
   protected override int RequestsMade => _requests;



   public GitHubListerContractTests()
   {
      // Until a test says otherwise: no organization is known, and every request is counted.
      _gateway.OrgPages = (_, _) => Asked<IReadOnlyList<GitHubRepo>>(() => throw NotFound());
      _gateway.OwnPages = _ => Asked<IReadOnlyList<GitHubRepo>>(() => []);
      _gateway.UserPages = (_, _) => Asked<IReadOnlyList<GitHubRepo>>(() => throw NotFound());
      _gateway.CurrentUser = () => Asked(() => "me");
      _gateway.OrgLogins = () => Asked<IReadOnlyList<string>>(() => []);
      _gateway.AccountKind = _ => Asked(() => GitHubAccountKind.NotFound);
   }



   private static NotFoundException NotFound() => new("nope", HttpStatusCode.NotFound);



   private Task<T> Asked<T>(Func<T> answer)
   {
      Interlocked.Increment(ref _requests);
      try
      {
         return Task.FromResult(answer());
      }
      catch(Exception failure)
      {
         return Task.FromException<T>(failure);
      }
   }



   private void EveryRequestFailsWith(Func<Exception> failure)
   {
      _gateway.OrgPages = (_, _) => Asked<IReadOnlyList<GitHubRepo>>(() => throw failure());
      _gateway.CurrentUser = () => Asked<string>(() => throw failure());
   }



   /// <inheritdoc/>
   protected override void GivenAnOrganization(string name, params string[] repositories)
   {
      Func<string, int, Task<IReadOnlyList<GitHubRepo>>> before = _gateway.OrgPages;
      _gateway.OrgPages = (organization, page) => string.Equals(organization, name, StringComparison.Ordinal)
          ? Asked<IReadOnlyList<GitHubRepo>>(() => page == 1
              ? [.. repositories.Select(repository => new GitHubRepo(repository, $"https://github.example.test/{name}/{repository}.git", "main", false))]
              : [])
          : before(organization, page);
   }



   /// <inheritdoc/>
   protected override void GivenTheTokenBelongsTo(string account, params string[] organizations)
   {
      _gateway.CurrentUser = () => Asked(() => account);
      _gateway.OrgLogins = () => Asked<IReadOnlyList<string>>(() => organizations);
   }



   /// <inheritdoc/>
   protected override void GivenNothingIsCalled(string name)
   {
      // Nothing to set up: what this gateway does not know is not found.
   }



   /// <inheritdoc/>
   protected override void GivenTheTokenIsRejected()
       => EveryRequestFailsWith(() => new AuthorizationException(HttpStatusCode.Unauthorized, null));



   /// <inheritdoc/>
   protected override void GivenTheForgeFails()
       => EveryRequestFailsWith(() => new ApiException("down", HttpStatusCode.ServiceUnavailable));
}
