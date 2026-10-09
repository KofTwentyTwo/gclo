/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using Octokit;


namespace gclo.Engine;


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
      IReadOnlyList<Organization> organizations = await _client.Organization
          .GetAllForCurrent(new ApiOptions { PageSize = IGitHubGateway.PageSize })
          .ConfigureAwait(false);
      return organizations.Select(o => o.Login).ToList();
   }



   public async Task<GitHubAccountKind> GetAccountKindAsync(string login)
   {
      try
      {
         User account = await _client.User.Get(login).ConfigureAwait(false);
         return account.Type == AccountType.Organization ? GitHubAccountKind.Organization : GitHubAccountKind.User;
      }
      catch(NotFoundException)
      {
         return GitHubAccountKind.NotFound;
      }
   }



   private static ApiOptions Page(int page)
       => new() { PageSize = IGitHubGateway.PageSize, PageCount = 1, StartPage = page };



   private static List<GitHubRepo> Map(IReadOnlyList<Repository> repositories)
       => repositories.Select(r => new GitHubRepo(r.Name, r.CloneUrl, r.DefaultBranch, r.Archived)).ToList();
}
