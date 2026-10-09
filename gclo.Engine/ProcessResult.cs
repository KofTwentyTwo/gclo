/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>Outcome of one external process: exit code plus captured output.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
