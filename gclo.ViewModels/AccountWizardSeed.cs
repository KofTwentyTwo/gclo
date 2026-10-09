/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.ViewModels;


/// <summary>
/// The connection values a Quick Sync workspace hands to the account wizard so a
/// working ad-hoc setup can be saved as an account without re-entering anything
/// (the seeded <see cref="AccountWizardViewModel"/> constructor).
/// </summary>
/// <param name="Token">The token in effect; written to the vault on save.</param>
/// <param name="Organization">The organization (or user) the workspace is connected to.</param>
/// <param name="TargetRoot">The workspace's target folder (not the org subfolder).</param>
/// <param name="CreateOrgSubfolder">Whether clones go under TargetRoot\Organization.</param>
/// <param name="MaxConcurrency">The workspace's parallel clone count.</param>
public sealed record AccountWizardSeed(
    string Token,
    string Organization,
    string TargetRoot,
    bool CreateOrgSubfolder,
    int MaxConcurrency)
{
   /// <summary>Redacts the token: this record must never print it.</summary>
   public override string ToString() =>
       $"AccountWizardSeed {{ Token = [redacted], Organization = {Organization}, TargetRoot = {TargetRoot}, "
       + $"CreateOrgSubfolder = {CreateOrgSubfolder}, MaxConcurrency = {MaxConcurrency} }}";
}
