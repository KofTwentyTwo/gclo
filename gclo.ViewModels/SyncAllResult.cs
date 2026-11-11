/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.ViewModels;


/// <summary>
/// Outcome of one sync-all pass over the account workspaces.
/// </summary>
/// <param name="Ran">Accounts whose sync ran to its end (with or without failed repositories).</param>
/// <param name="Skipped">
/// Accounts whose turn came and that were left out with nothing wrong: they were
/// busy, had nothing to sync, or their own sync was canceled.
/// </param>
/// <param name="WasCanceled">True when cancellation stopped the queue before every account was processed.</param>
/// <param name="Failed">
/// Accounts that need attention: their sync ran and a repository failed, or they
/// could not be synced. The second kind is counted in <paramref name="CouldNotSync"/> too.
/// </param>
/// <param name="NotStarted">Accounts whose turn never came, because the pass was canceled before it.</param>
/// <param name="CouldNotSync">
/// Accounts whose turn came and that could not be synced: no token, repositories
/// that could not be loaded, a sync that could not start, or their own sync canceled
/// after a repository had failed.
/// </param>
/// <remarks>
/// <paramref name="Ran"/>, <paramref name="Skipped"/>, <paramref name="CouldNotSync"/>
/// and <paramref name="NotStarted"/> count every account of the pass exactly once.
/// </remarks>
public sealed record SyncAllResult(int Ran, int Skipped, bool WasCanceled, int Failed = 0, int NotStarted = 0, int CouldNotSync = 0)
{
   /// <summary>The accounts of the pass.</summary>
   public int Total => Ran + Skipped + CouldNotSync + NotStarted;
}
