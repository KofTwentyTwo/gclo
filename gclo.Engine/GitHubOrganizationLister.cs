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
         // live under /users, not /orgs), so it heads the list.
         userLogin = await gateway.GetCurrentUserLoginAsync().ConfigureAwait(false);

         try
         {
            organizations = await gateway.GetOrganizationLoginsAsync().ConfigureAwait(false);
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
      catch(AuthorizationException ex)
      {
         throw new GitHubAccessException(GitHubAccessKind.Unauthorized, "GitHub rejected the token (401). Check the PAT.", ex);
      }
      catch(RateLimitExceededException ex)
      {
         throw new GitHubAccessException(
             GitHubAccessKind.RateLimited, $"GitHub API rate limit exceeded; it resets at {ex.Reset:u}.", ex);
      }
      catch(SecondaryRateLimitExceededException ex)
      {
         throw new GitHubAccessException(
             GitHubAccessKind.RateLimited,
             "GitHub's secondary rate limit was hit (too many requests in a short time); retry in a minute.", ex);
      }
      catch(AbuseException ex)
      {
         throw new GitHubAccessException(
             GitHubAccessKind.RateLimited,
             $"GitHub's secondary rate limit was hit (too many requests in a short time); retry in {ex.RetryAfterSeconds ?? 60} seconds.", ex);
      }
      catch(LoginAttemptsExceededException ex)
      {
         throw new GitHubAccessException(
             GitHubAccessKind.RateLimited,
             "GitHub temporarily blocked this token after too many failed attempts; wait a few minutes and retry.", ex);
      }

      cancellationToken.ThrowIfCancellationRequested();

      var result = new List<string> { userLogin };
      result.AddRange(organizations
          .Where(login => !string.Equals(login, userLogin, StringComparison.OrdinalIgnoreCase))
          .OrderBy(login => login, StringComparer.OrdinalIgnoreCase));
      return result;
   }
}
