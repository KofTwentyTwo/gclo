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

   /// <summary>Finished, but at least one repository failed.</summary>
   Failed,

   /// <summary>Not run: busy, unloadable, nothing to sync, or the queue was canceled.</summary>
   Skipped,
}
