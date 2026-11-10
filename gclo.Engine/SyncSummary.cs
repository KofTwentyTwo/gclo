/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>Aggregate outcome of an organization sync.</summary>
/// <param name="Total">Number of repositories the sync set out to process.</param>
/// <param name="Cloned">Repositories cloned for the first time.</param>
/// <param name="Updated">Existing repositories fetched and fast-forwarded.</param>
/// <param name="Failed">Repositories that failed; a failure never aborts the others.</param>
/// <param name="Canceled">Repositories canceled before or during processing.</param>
/// <param name="WasCanceled">
/// Whether the run was cut short by cancellation. From the engine it is true exactly
/// when <paramref name="Canceled"/> is above zero: a cancellation that arrives after
/// the last repository finished leaves a complete run. A caller that does more work
/// around the engine (listing first, recovering paths afterwards) sets it when that
/// work was cut short.
/// </param>
public sealed record SyncSummary(int Total, int Cloned, int Updated, int Failed, int Canceled, bool WasCanceled);
