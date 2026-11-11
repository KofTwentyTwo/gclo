/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.Engine;


namespace gclo.ViewModels;


/// <summary>
/// The one place that turns an <see cref="Account"/> into the token to use for it:
/// its own vault entry for <see cref="TokenSource.Own"/>, the default token from
/// Settings for <see cref="TokenSource.Default"/>. The desktop workspace, Sync All,
/// the account wizard and the CLI all resolve through here, so no consumer reads
/// the vault for an account directly (docs/plans/default-token-ux.md).
/// </summary>
public sealed class AccountTokenResolver
{
   private readonly ITokenVault _vault;



   /// <summary>Resolves against <paramref name="vault"/>.</summary>
   public AccountTokenResolver(ITokenVault vault)
   {
      ArgumentNullException.ThrowIfNull(vault);
      _vault = vault;
   }



   /// <summary>
   /// The token for <paramref name="account"/>, or null when its source holds
   /// nothing (no default token saved, or the account's own entry is gone).
   /// </summary>
   public string? TryResolve(Account account)
   {
      ArgumentNullException.ThrowIfNull(account);
      if(account.TokenSource == TokenSource.Default)
      {
         ///////////////////////////////////////////////////////////////////////////
         // The default token of the place the account is on, and of no other: it //
         // is looked up by that place. An account whose place cannot be used has //
         // none.                                                                  //
         ///////////////////////////////////////////////////////////////////////////
         return account.TryGetEndpoint(out ForgeEndpoint? place, out _) ? DefaultTokens.For(_vault, place) : null;
      }

      string? token = _vault.TryRetrieve(account.TokenEntryId);
      return string.IsNullOrEmpty(token) ? null : token;
   }



   /// <summary>True when the vault holds a default token for <paramref name="place"/>.</summary>
   public bool HasDefaultTokenFor(ForgeEndpoint place) => DefaultTokens.For(_vault, place) is not null;



   /// <summary>
   /// Why <see cref="TryResolve"/> returned null for <paramref name="account"/>, worded
   /// for the user and naming the source, so every "no token" message says what to fix:
   /// for a token of the account's own, the entry as this vault names it. Never
   /// contains a token.
   /// </summary>
   public string MissingDescription(Account account)
   {
      ArgumentNullException.ThrowIfNull(account);
      if(account.TokenSource == TokenSource.Default)
      {
         return account.Forge == ForgeKind.GitHub
             ? "uses the default token, but no default token is saved (desktop app: Settings)"
             : $"uses the default token for {account.DescribePlace()}, but none is saved for it (desktop app: Settings)";
      }
      return $"has no token in {_vault.DescribeEntry(account.TokenEntryId)}";
   }
}
