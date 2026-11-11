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
      if(account.TokenSource == TokenSource.Default && account.Forge != ForgeKind.GitHub)
      {
         ///////////////////////////////////////////////////////////////////////////
         // The default token is a GitHub token. It is never handed to an account //
         // on another forge, however such an account came to say it uses it.     //
         ///////////////////////////////////////////////////////////////////////////
         return null;
      }

      string? token = account.TokenSource == TokenSource.Default
          ? _vault.TryRetrieve(AppSettings.DefaultTokenVaultId)
          : _vault.TryRetrieve(account.TokenEntryId);
      return string.IsNullOrEmpty(token) ? null : token;
   }



   /// <summary>True when the vault holds a default token.</summary>
   public bool HasDefaultToken => !string.IsNullOrEmpty(_vault.TryRetrieve(AppSettings.DefaultTokenVaultId));



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
             : "is set to use the default token, which is a GitHub token and is not used for another forge; give the account a token of its own";
      }
      return $"has no token in {_vault.DescribeEntry(account.TokenEntryId)}";
   }
}
