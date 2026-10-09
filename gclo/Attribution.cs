/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo;


/// <summary>One third-party component credited in the About dialog.</summary>
public sealed record Attribution(string Name, string License)
{
   /// <summary>The license as rendered after the name.</summary>
   public string LicenseText => " — " + License;
}
