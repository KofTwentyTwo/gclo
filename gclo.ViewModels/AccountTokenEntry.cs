/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.Engine;


namespace gclo.ViewModels;


/// <summary>
/// Which vault entry holds an account's own token. For a GitHub account it is the
/// account's id, as it always was. For an account on another forge it is a
/// different id, derived from the account's.
/// </summary>
/// <remarks>
/// A version of gclo from before forges existed reads every account as a GitHub
/// account and looks its token up under the account's id. If the token of a Gitea
/// account were kept there, that version would send it to GitHub. Kept under an id
/// it does not look up, the account has no token as far as that version can tell,
/// and it says so instead.
/// </remarks>
internal static class AccountTokenEntry
{
   /// <summary>What an account's id is combined with for a forge that is not GitHub. Never to be changed: saved tokens are found by it.</summary>
   private static readonly byte[] s_otherForge = new Guid("6f0c2a51-9d3e-4b77-8a14-c5e2f0b39d68").ToByteArray();



   /// <summary>The id of the vault entry for the token of the account <paramref name="accountId"/> on <paramref name="forge"/>.</summary>
   public static Guid For(Guid accountId, ForgeKind forge)
   {
      if(forge == ForgeKind.GitHub)
      {
         return accountId;
      }

      byte[] id = accountId.ToByteArray();
      for(int i = 0; i < id.Length; i++)
      {
         id[i] ^= s_otherForge[i];
      }
      return new Guid(id);
   }
}
