/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Security.Cryptography;
using System.Text;
using gclo.Engine;


namespace gclo.ViewModels;


/// <summary>
/// The default tokens saved in Settings: one for GitHub, and one for a Gitea
/// server. Quick Sync fills in the one of the place it is on, and an account that
/// uses "the default token" gets the one of the place it is on.
/// </summary>
/// <remarks>
/// A default token belongs to one place and is never used for another. That is not
/// a check somebody has to remember to make: the vault entry is named after the
/// place (for a Gitea server, after its address), so asking for the default token
/// of a place finds the one that was saved for exactly that place, or none.
/// </remarks>
public static class DefaultTokens
{
   /// <summary>
   /// The vault entry that holds the default token of <paramref name="place"/>:
   /// for GitHub the one it always was; for a Gitea server one made from the
   /// server's address. Never to be changed: saved tokens are found by it.
   /// </summary>
   public static Guid EntryFor(ForgeEndpoint place)
   {
      ArgumentNullException.ThrowIfNull(place);
      if(place.Server is null)
      {
         return AppSettings.DefaultTokenVaultId;
      }

      byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"gclo default token\n{place.Kind}\n{place.Server.AbsoluteUri}"));
      return new Guid(hash.AsSpan(0, 16));
   }



   /// <summary>The default token saved for <paramref name="place"/>, or null when none is saved for it.</summary>
   public static string? For(ITokenVault vault, ForgeEndpoint place)
   {
      ArgumentNullException.ThrowIfNull(vault);
      string? token = vault.TryRetrieve(EntryFor(place));
      return string.IsNullOrEmpty(token) ? null : token;
   }
}
