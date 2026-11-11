/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.Engine;


namespace gclo.ViewModels;


/// <summary>
/// The listers of an account whose forge or server cannot be used as they are
/// stored (an accounts.json edited by hand, or written by a version that knows a
/// forge this one does not). It asks no server anything: every call fails with the
/// reason, so the account's token goes nowhere.
/// </summary>
internal sealed class UnusableAccountLister : IRepositoryLister, IOrganizationLister
{
   private readonly string _message;



   /// <summary>Creates the lister for the account <paramref name="account"/>, which cannot be used because of <paramref name="reason"/>.</summary>
   public UnusableAccountLister(string account, string reason)
       => _message = $"Account '{account}' cannot be used as it is saved: {reason}. Delete the account and add it again.";



   /// <inheritdoc/>
   public Task<IReadOnlyList<RepoDescriptor>> ListOrganizationRepositoriesAsync(
       string organization, string token, CancellationToken cancellationToken = default)
       => Task.FromException<IReadOnlyList<RepoDescriptor>>(new ForgeAccessException(ForgeAccessKind.NotFound, _message));



   /// <inheritdoc/>
   public Task<IReadOnlyList<string>> ListOrganizationsAsync(string token, CancellationToken cancellationToken = default)
       => Task.FromException<IReadOnlyList<string>>(new ForgeAccessException(ForgeAccessKind.NotFound, _message));
}
