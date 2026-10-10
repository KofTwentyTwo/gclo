/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

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
      string? token = account.TokenSource == TokenSource.Default
          ? _vault.TryRetrieve(AppSettings.DefaultTokenVaultId)
          : _vault.TryRetrieve(account.Id);
      return string.IsNullOrEmpty(token) ? null : token;
   }



   /// <summary>True when the vault holds a default token.</summary>
   public bool HasDefaultToken => !string.IsNullOrEmpty(_vault.TryRetrieve(AppSettings.DefaultTokenVaultId));



   /// <summary>
   /// Why <see cref="TryResolve"/> returned null for <paramref name="account"/>, worded
   /// for the user and naming the source, so every "no token" message says what to fix.
   /// Never contains a token.
   /// </summary>
   public static string MissingDescription(Account account)
   {
      ArgumentNullException.ThrowIfNull(account);
      return account.TokenSource == TokenSource.Default
          ? "uses the default token, but no default token is saved (desktop app: Settings)"
          : $"has no token in Windows Credential Manager (entry 'gclo:account:{account.Id:N}')";
   }
}
