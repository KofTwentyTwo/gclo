/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.ViewModels;


/// <summary>What a sync-all pass is doing, or how the last one ended; see <see cref="SyncAllStatus"/>.</summary>
public enum SyncAllOutcome
{
   /// <summary>No pass has run yet.</summary>
   None,

   /// <summary>A pass is running.</summary>
   Running,

   /// <summary>Every account that ran finished without a failed repository.</summary>
   Finished,

   /// <summary>The pass finished, and at least one account had failures or could not be synced.</summary>
   FinishedWithFailures,

   /// <summary>The pass was canceled before every account had its turn.</summary>
   Canceled,

   /// <summary>The pass stopped on an unexpected error.</summary>
   Stopped,
}
