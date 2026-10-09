/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Cli;


/// <summary>One Windows-invalid path behind a failure, as the validator reported it.</summary>
internal sealed record SyncInvalidPath(string Path, string Reason, string? SuggestedName);
