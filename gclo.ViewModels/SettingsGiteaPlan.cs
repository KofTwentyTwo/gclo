/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.Engine;


namespace gclo.ViewModels;


/// <summary>
/// What Save does with the Gitea group of the Settings dialog
/// (<see cref="SettingsGiteaState.Plan"/>): keep, store or remove the default
/// token, for which server, or the reason it cannot be saved as it stands.
/// </summary>
/// <param name="Action">What happens to the default token.</param>
/// <param name="Place">The server the action is about; null when nothing is stored or removed.</param>
/// <param name="Error">Why the group cannot be saved as it stands; empty when it can.</param>
public sealed record SettingsGiteaPlan(SettingsTokenAction Action, ForgeEndpoint? Place, string Error)
{
   /// <summary>True when the dialog must not be saved: the server address or the token needs correcting first.</summary>
   public bool IsError => Error.Length > 0;
}
