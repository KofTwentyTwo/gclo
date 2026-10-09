/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Cli;


/// <summary>One status transition in 'gclo sync --json-lines'; <see cref="Type"/> is always "progress".</summary>
internal sealed record SyncProgressLine(string Type, string Repo, string Status, string? Error);
