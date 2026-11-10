/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using Octokit;


namespace gclo.Engine;


/// <summary>Lists the authenticated user's organizations through the GitHub REST API.</summary>
public sealed class GitHubOrganizationLister : IOrganizationLister
{
   private readonly Func<string, IGitHubGateway> _gatewayFactory;



   /// <summary>Production wiring: the process-wide Octokit gateway for the call's token (connection reuse).</summary>
   public GitHubOrganizationLister()
       : this(OctokitGatewayCache.Get)
   {
   }



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
         // live under /users, not /orgs), so it heads the list. The gateway cannot
         // be canceled, so each request is awaited with the token: one that hangs
         // is abandoned and ends on its own, at the HTTP timeout.
         userLogin = await GitHubFailures.WaitFor(gateway.GetCurrentUserLoginAsync(), cancellationToken)
             .ConfigureAwait(false);

         try
         {
            organizations = await GitHubFailures.WaitFor(gateway.GetOrganizationLoginsAsync(), cancellationToken)
                .ConfigureAwait(false);
         }
         catch(ForbiddenException ex)
             when(ex is not RateLimitExceededException
                 and not SecondaryRateLimitExceededException
                 and not AbuseException
                 and not LoginAttemptsExceededException)
         {
            // Degraded mode: classic PATs without the read:org scope get 403
            // from /user/orgs even though the token is otherwise fine. The
            // personal account is still a usable sync target, so return just
            // that instead of failing the whole listing. Throttling 403s
            // (secondary rate limit, abuse detection, login attempts) are NOT a
            // scope problem and must not be degraded into "no organizations" (#31).
            return [userLogin];
         }
      }
      catch(Exception ex) when(GitHubFailures.Knows(ex, cancellationToken))
      {
         // What the failure means to a caller is decided in one place, for both listers.
         throw GitHubFailures.Translate(ex);
      }

      cancellationToken.ThrowIfCancellationRequested();

      var result = new List<string> { userLogin };
      result.AddRange(organizations
          .Where(login => !string.Equals(login, userLogin, StringComparison.OrdinalIgnoreCase))
          .OrderBy(login => login, StringComparer.OrdinalIgnoreCase));
      return result;
   }
}
