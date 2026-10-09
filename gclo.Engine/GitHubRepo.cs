/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>One repository as the GitHub API describes it, in the fields gclo uses.</summary>
internal readonly record struct GitHubRepo(string Name, string CloneUrl, string? DefaultBranch, bool IsArchived);
