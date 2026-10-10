/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Cli;


/// <summary>
/// One saved account in the JSON listing of 'gclo accounts --json'.
/// <see cref="LastSync"/> is the UTC time of the last completed sync, or null
/// when the account has never synced; <see cref="Id"/> is the key of its
/// Credential Manager entry ('gclo:account:&lt;id&gt;').
/// </summary>
internal sealed record AccountSummary(
    string Id,
    string Name,
    string Description,
    string Organization,
    string TargetRoot,
    bool CreateOrgSubfolder,
    int MaxConcurrency,
    DateTimeOffset? LastSync,
    string? LastSyncSummary,
    string TokenSource);
