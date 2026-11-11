/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.Engine;


namespace gclo.ViewModels;


/// <summary>
/// What a Quick Sync connection that worked consisted of, without its token: the
/// place, the organization and where the clones go. It is what the app remembers
/// between runs, so Quick Sync opens the way it was left. A token is never part of
/// it: Quick Sync keeps none.
/// </summary>
/// <param name="Place">GitHub, or the Gitea server.</param>
/// <param name="Organization">The organization (or user) that was loaded.</param>
/// <param name="TargetFolder">The target folder; empty when none was chosen.</param>
/// <param name="CreateOrgSubfolder">Whether clones go under TargetFolder\Organization.</param>
public sealed record QuickSyncConnection(ForgeEndpoint Place, string Organization, string TargetFolder, bool CreateOrgSubfolder);
