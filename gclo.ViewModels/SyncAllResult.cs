/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.ViewModels;


/// <summary>
/// Outcome of one sync-all pass over the account workspaces.
/// </summary>
/// <param name="Ran">Accounts whose sync ran to completion (successfully or not).</param>
/// <param name="Skipped">Accounts skipped because they were busy, could not load, or had nothing to sync.</param>
/// <param name="WasCanceled">True when cancellation stopped the queue before every account was processed.</param>
public sealed record SyncAllResult(int Ran, int Skipped, bool WasCanceled);
