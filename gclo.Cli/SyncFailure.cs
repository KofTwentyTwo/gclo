/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Cli;


/// <summary>
/// One failed repository in the JSON summary. <see cref="InvalidPaths"/> is present
/// (non-null) only when the failure was Windows path validation — the full list, not
/// the three-path excerpt the message carries.
/// </summary>
internal sealed record SyncFailure(string Repo, string Error, IReadOnlyList<SyncInvalidPath>? InvalidPaths = null);
