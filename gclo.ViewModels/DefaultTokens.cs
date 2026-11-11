/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.Engine;


namespace gclo.ViewModels;


/// <summary>
/// The default token saved in Settings for a place, which Quick Sync fills in when
/// it is on that place. A default token belongs to one place and is never offered
/// for another.
/// </summary>
public static class DefaultTokens
{
   /// <summary>
   /// The default token for <paramref name="place"/>, or null when none is saved for
   /// it. Today Settings holds one for GitHub only.
   /// </summary>
   public static string? For(ITokenVault vault, ForgeEndpoint place)
   {
      ArgumentNullException.ThrowIfNull(vault);
      ArgumentNullException.ThrowIfNull(place);
      if(place.Kind != ForgeKind.GitHub)
      {
         return null;
      }

      string? token = vault.TryRetrieve(AppSettings.DefaultTokenVaultId);
      return string.IsNullOrEmpty(token) ? null : token;
   }
}
