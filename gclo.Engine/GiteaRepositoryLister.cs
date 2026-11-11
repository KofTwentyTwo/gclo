/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Net;


namespace gclo.Engine;


/// <summary>
/// Lists the repositories of an organization or account on a Gitea server (or one
/// that speaks its API) through the server's REST API. The server is the one given
/// at construction: the token of a call is sent there and nowhere else, and the
/// clone address of every repository is on that server.
/// </summary>
public sealed class GiteaRepositoryLister : IRepositoryLister
{
   private readonly GiteaApi _api;



   /// <summary>Creates the lister for the server at <paramref name="server"/>.</summary>
   /// <param name="server">The server's address, as <see cref="GiteaServerUrl.TryNormalize"/> accepts it (HTTPS).</param>
   /// <exception cref="ArgumentException"><paramref name="server"/> is not an address a Gitea account can have.</exception>
   public GiteaRepositoryLister(Uri server)
       : this(new GiteaApi(server))
   {
   }



   /// <summary>Test seam: the requests go through <paramref name="api"/>.</summary>
   internal GiteaRepositoryLister(GiteaApi api)
       => _api = api ?? throw new ArgumentNullException(nameof(api));



   /// <inheritdoc/>
   public async Task<IReadOnlyList<RepoDescriptor>> ListOrganizationRepositoriesAsync(
       string organization, string token, CancellationToken cancellationToken = default)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(organization);
      ArgumentException.ThrowIfNullOrWhiteSpace(token);
      cancellationToken.ThrowIfCancellationRequested();

      ///////////////////////////////////////////////////////////////////////////
      // The name goes into request paths, into clone addresses and, with the  //
      // org-subfolder option and the WSL recovery, into local paths. Anything //
      // that is not one plain name is refused before a request is made.       //
      ///////////////////////////////////////////////////////////////////////////
      if(!RepositoryPathResolver.TryValidateName(organization, out string? invalidName))
      {
         throw new ForgeAccessException(
             ForgeAccessKind.NotFound,
             $"'{organization}' is not an organization or account name ({invalidName}). Check the spelling.");
      }

      string name = Uri.EscapeDataString(organization);
      List<GiteaRepository> repositories;
      try
      {
         try
         {
            repositories = await _api
                .ListAsync($"orgs/{name}/repos", token, GiteaJsonContext.Default.ListGiteaRepository, cancellationToken)
                .ConfigureAwait(false);
         }
         catch(GiteaStatusException notAnOrganization)
             when(notAnOrganization.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
         {
            ///////////////////////////////////////////////////////////////////////
            // No organization of that name that the token can list: it is a    //
            // user account, or nothing. (A token that may not read             //
            // organizations is refused here whatever the name is, its own      //
            // account included: that is not yet an answer either.) The token's //
            // own account gets its own repositories (the private ones too);    //
            // another account the ones the token can see there.                //
            ///////////////////////////////////////////////////////////////////////
            string own = await _api.GetOwnNameAsync(token, cancellationToken).ConfigureAwait(false);
            bool isOwn = string.Equals(own, organization, StringComparison.OrdinalIgnoreCase);
            try
            {
               repositories = await _api
                   .ListAsync(isOwn ? "user/repos" : $"users/{name}/repos", token, GiteaJsonContext.Default.ListGiteaRepository, cancellationToken)
                   .ConfigureAwait(false);
            }
            catch(GiteaStatusException nobody) when(nobody.Status == HttpStatusCode.NotFound && !isOwn)
            {
               if(notAnOrganization.Status == HttpStatusCode.Forbidden)
               {
                  // Not an account either: the refusal of the organization is the answer.
                  throw _api.Translate(notAnOrganization);
               }
               throw new ForgeAccessException(
                   ForgeAccessKind.NotFound,
                   $"'{organization}' was found neither as an organization nor as a user account on {_api.Host} (404), "
                   + "or the token cannot see it. Check the spelling.",
                   nobody);
            }
         }
      }
      catch(Exception failure) when(failure is not ForgeAccessException && _api.Knows(failure, cancellationToken))
      {
         // What the failure means to a caller is decided in one place, for both listers.
         throw _api.Translate(failure);
      }

      cancellationToken.ThrowIfCancellationRequested();

      ///////////////////////////////////////////////////////////////////////////
      // The list of the token's own repositories also holds repositories of   //
      // other owners that it may work on. Only what the named account owns    //
      // is its repository: the rest would be cloned under a wrong name, from  //
      // an address that is not theirs.                                        //
      ///////////////////////////////////////////////////////////////////////////
      return [.. repositories
          .Where(r => !string.IsNullOrWhiteSpace(r.Name)
                      && (r.Owner?.Name is null || string.Equals(r.Owner.Name, organization, StringComparison.OrdinalIgnoreCase)))
          // A repository created while the list is read shifts the pages and can
          // repeat an item; duplicates would race two clones into one folder.
          .DistinctBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
          .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
          .Select(r => new RepoDescriptor(
              r.Name!,
              _api.CloneUrl(r.Owner?.Name ?? organization, r.Name!),
              r.Empty || string.IsNullOrWhiteSpace(r.DefaultBranch) ? null : r.DefaultBranch,
              r.Archived))];
   }
}
