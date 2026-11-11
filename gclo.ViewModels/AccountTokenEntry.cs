/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Security.Cryptography;
using System.Text;


namespace gclo.ViewModels;


/// <summary>
/// Which vault entry holds an account's own token. For a GitHub account it is the
/// account's id, as it always was. For an account on another forge it is an id
/// made from the account's id, the name of its forge and the address of its
/// server, exactly as the account's file states them.
/// </summary>
/// <remarks>
/// <para>
/// The entry belongs to one account at one place. An account's forge and server
/// cannot be changed through gclo (<see cref="AccountsStore.Save"/>); when the file
/// is changed by hand to name another server, the entry is not found there either,
/// and the account has no token instead of sending the one it had to the new
/// address. A record that names another account's id finds nothing for the same
/// reason.
/// </para>
/// <para>
/// Never to be changed: saved tokens are found by it. What goes in is the text of
/// the file, not an address that was parsed, so a later change to how an address
/// is normalized changes no entry.
/// </para>
/// </remarks>
internal static class AccountTokenEntry
{
   /// <summary>The id of the vault entry for the token of <paramref name="account"/>.</summary>
   public static Guid For(Account account)
   {
      if(account.ForgeName is null)
      {
         return account.Id;
      }

      byte[] hash = SHA256.HashData(
          Encoding.UTF8.GetBytes($"gclo account token\n{account.Id:N}\n{account.ForgeName}\n{account.Server}"));
      return new Guid(hash.AsSpan(0, 16));
   }
}
