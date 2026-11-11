/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.Engine;


namespace gclo.ViewModels;


/// <summary>
/// The listers that belong to an account: those of its forge and, for a Gitea
/// account, of its server. The desktop app and the command line both ask here, so
/// an account's token is never handed to the listers of another forge.
/// </summary>
public static class AccountListers
{
   /// <summary>The lister of the repositories on <paramref name="account"/>'s forge.</summary>
   public static IRepositoryLister RepositoryListerFor(Account account)
   {
      ArgumentNullException.ThrowIfNull(account);
      return account.TryGetEndpoint(out ForgeEndpoint? endpoint, out string? reason)
          ? endpoint.CreateRepositoryLister()
          : new UnusableAccountLister(account.Name, reason);
   }



   /// <summary>The lister of the organizations on <paramref name="account"/>'s forge.</summary>
   public static IOrganizationLister OrganizationListerFor(Account account)
   {
      ArgumentNullException.ThrowIfNull(account);
      return account.TryGetEndpoint(out ForgeEndpoint? endpoint, out string? reason)
          ? endpoint.CreateOrganizationLister()
          : new UnusableAccountLister(account.Name, reason);
   }
}
