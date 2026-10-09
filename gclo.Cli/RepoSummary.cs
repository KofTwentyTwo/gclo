/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Cli;


/// <summary>One repository in the JSON listing of 'gclo repos --json'.</summary>
internal sealed record RepoSummary(string Name, string? DefaultBranch, bool Archived, string CloneUrl);
