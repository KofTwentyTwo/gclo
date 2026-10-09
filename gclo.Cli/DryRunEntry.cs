/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Cli;


/// <summary>One repository of 'gclo sync --dry-run --json': what the run would do with it.</summary>
internal sealed record DryRunEntry(string Repo, string Action);
