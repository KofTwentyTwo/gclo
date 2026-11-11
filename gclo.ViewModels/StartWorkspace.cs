/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.ViewModels;


/// <summary>
/// Which workspace the app opens on. The start screen is the thing the user most
/// likely came to do: where they left off, and when nothing says where that was,
/// their first account; Quick Sync when there is no account.
/// </summary>
public static class StartWorkspace
{
   /// <summary>The id that stands for Quick Sync, which has no account.</summary>
   public static readonly Guid QuickSync = Guid.Empty;



   /// <summary>
   /// The workspace to open: <paramref name="last"/> when it is Quick Sync or an
   /// account that still exists; otherwise the first of <paramref name="accounts"/>
   /// (they come sorted by name); otherwise Quick Sync.
   /// </summary>
   /// <param name="accounts">The saved accounts, in the order the pane lists them.</param>
   /// <param name="last">The workspace that was open last; null when nothing is recorded.</param>
   public static Guid Choose(IReadOnlyList<Account> accounts, Guid? last)
   {
      ArgumentNullException.ThrowIfNull(accounts);
      if(last is Guid id && (id == QuickSync || accounts.Any(a => a.Id == id)))
      {
         return id;
      }
      return accounts.Count > 0 ? accounts[0].Id : QuickSync;
   }
}
