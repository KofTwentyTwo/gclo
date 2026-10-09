/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Cli;


/// <summary>
/// The single-line JSON document printed by 'gclo sync --json', and the last line of
/// 'gclo sync --json-lines'. <see cref="Type"/> is always "summary" so a line reader
/// can tell it from progress lines.
/// </summary>
internal sealed record SyncJsonResult(
    string Type,
    int Total,
    int Cloned,
    int Updated,
    int Failed,
    int Canceled,
    bool WasCanceled,
    IReadOnlyList<SyncFailure> Failures,
    IReadOnlyList<SyncSanitized> Sanitized);
