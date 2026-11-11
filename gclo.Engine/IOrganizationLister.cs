/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>Lists the organizations a token can see on a forge (GitHub, a Gitea server).</summary>
public interface IOrganizationLister
{
   /// <summary>
   /// Returns the token's own account, then the names of the organizations its
   /// user belongs to. May hold the account alone for tokens without organization
   /// read access (GitHub: fine-grained PATs, classic PATs missing read:org; Gitea:
   /// a token without read:organization) — callers should still allow manual
   /// organization entry.
   /// </summary>
   /// <exception cref="ForgeAccessException">The forge refused the request, or could not be reached.</exception>
   Task<IReadOnlyList<string>> ListOrganizationsAsync(string token, CancellationToken cancellationToken = default);
}
