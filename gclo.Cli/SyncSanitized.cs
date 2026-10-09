/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Cli;


/// <summary>
/// One repository that '--sanitize-paths' checked out with renames and skips: it
/// counts as cloned, but its working tree is incomplete by <see cref="Skipped"/>
/// paths, which a mirroring pipeline needs to know.
/// </summary>
internal sealed record SyncSanitized(string Repo, int Renamed, int Skipped, IReadOnlyList<string> SkippedPaths);
