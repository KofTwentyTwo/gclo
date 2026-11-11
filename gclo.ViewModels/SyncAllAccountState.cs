/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.ViewModels;


/// <summary>Where one account stands within a sync-all pass; feeds the sidebar badges.</summary>
public enum SyncAllAccountState
{
   /// <summary>Scheduled in this pass, waiting its turn.</summary>
   Queued,

   /// <summary>The one account whose load/sync is in flight right now.</summary>
   Running,

   /// <summary>Finished with no failed repositories.</summary>
   Succeeded,

   /// <summary>
   /// Needs attention: its sync finished with at least one failed repository, or it
   /// could not be synced (no token, repositories that could not be loaded, a sync
   /// that could not start).
   /// </summary>
   Failed,

   /// <summary>
   /// Not synced, and nothing is wrong with it: busy, nothing to sync, its own sync
   /// was canceled, or the queue was canceled before its turn.
   /// </summary>
   Skipped,
}
