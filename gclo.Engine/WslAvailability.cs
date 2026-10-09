/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>Whether a clone into WSL can be attempted on this machine, and why not when it cannot.</summary>
/// <param name="Available">True when wsl.exe exists, a default distribution starts, and git runs inside it.</param>
/// <param name="Detail">The git version when available; the reason otherwise (shown to the user).</param>
public sealed record WslAvailability(bool Available, string Detail);
