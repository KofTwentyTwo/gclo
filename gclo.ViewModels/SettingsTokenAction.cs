/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.ViewModels;


/// <summary>What Save does with the default-token box (<see cref="SettingsTokenState.Plan"/>).</summary>
public enum SettingsTokenAction
{
   /// <summary>Leave the vault as it is: the mask is untouched, or nothing was saved and nothing typed.</summary>
   Keep = 0,

   /// <summary>Store what was typed as the default token (replacing a saved one).</summary>
   Store = 1,

   /// <summary>Delete the saved default token: the box was cleared or Remove was pressed.</summary>
   Remove = 2,
}
