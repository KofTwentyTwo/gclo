/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>What a GitHub login names, as far as the token can tell.</summary>
internal enum GitHubAccountKind
{
   /// <summary>A user account.</summary>
   User,

   /// <summary>An organization (whether or not the token can see its repositories).</summary>
   Organization,

   /// <summary>No account with that login is visible to the token.</summary>
   NotFound,
}
