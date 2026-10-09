/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using Octokit;


namespace gclo.Engine;


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



   /// <summary>Whether <paramref name="login"/> is a user, an organization, or unknown to the token.</summary>
   Task<GitHubAccountKind> GetAccountKindAsync(string login);
}
