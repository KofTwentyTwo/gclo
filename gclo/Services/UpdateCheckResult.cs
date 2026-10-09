/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Services;


/// <summary>
/// Result of an update check. <see cref="AvailableVersion"/> is the newer version found,
/// or null when already up to date; <see cref="Error"/> is non-null when the check itself failed.
/// </summary>
public sealed record UpdateCheckResult(string? AvailableVersion, string? Error);
