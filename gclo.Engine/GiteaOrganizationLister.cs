/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Net;


namespace gclo.Engine;


/// <summary>
/// Lists the account a token belongs to and the organizations it is a member of, on
/// a Gitea server (or one that speaks its API). The server is the one given at
/// construction: the token of a call is sent there and nowhere else.
/// </summary>
public sealed class GiteaOrganizationLister : IOrganizationLister
{
   private readonly GiteaApi _api;



   /// <summary>Creates the lister for the server at <paramref name="server"/>.</summary>
   /// <param name="server">The server's address, as <see cref="GiteaServerUrl.TryNormalize"/> accepts it (HTTPS).</param>
   /// <exception cref="ArgumentException"><paramref name="server"/> is not an address a Gitea account can have.</exception>
   public GiteaOrganizationLister(Uri server)
       : this(new GiteaApi(server))
   {
   }



   /// <summary>Test seam: the requests go through <paramref name="api"/>.</summary>
   internal GiteaOrganizationLister(GiteaApi api)
       => _api = api ?? throw new ArgumentNullException(nameof(api));



   /// <inheritdoc/>
   public async Task<IReadOnlyList<string>> ListOrganizationsAsync(string token, CancellationToken cancellationToken = default)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(token);
      cancellationToken.ThrowIfCancellationRequested();

      string own;
      List<GiteaAccount> organizations;
      try
      {
         // The token's own account is a valid sync target too, so it heads the list.
         own = await _api.GetOwnNameAsync(token, cancellationToken).ConfigureAwait(false);

         try
         {
            organizations = await _api
                .ListAsync("user/orgs", token, GiteaJsonContext.Default.ListGiteaAccount, cancellationToken)
                .ConfigureAwait(false);
         }
         catch(GiteaStatusException noScope) when(noScope.Status == HttpStatusCode.Forbidden)
         {
            ///////////////////////////////////////////////////////////////////////
            // Degraded mode: a token without the read:organization scope may   //
            // not list organizations, and is otherwise fine. The personal      //
            // account is still a usable sync target, and an organization can   //
            // be typed by hand.                                                //
            ///////////////////////////////////////////////////////////////////////
            return [own];
         }
      }
      catch(Exception failure) when(_api.Knows(failure, cancellationToken))
      {
         // What the failure means to a caller is decided in one place, for both listers.
         throw _api.Translate(failure);
      }

      cancellationToken.ThrowIfCancellationRequested();

      var result = new List<string> { own };
      result.AddRange(organizations
          .Select(organization => organization.Name)
          .OfType<string>()
          .Where(name => !string.Equals(name, own, StringComparison.OrdinalIgnoreCase))
          .Distinct(StringComparer.OrdinalIgnoreCase)
          .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
      return result;
   }
}
