/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>The kind of server an organization or account lives on.</summary>
public enum ForgeKind
{
   /// <summary>github.com.</summary>
   GitHub,

   /// <summary>A Gitea server, or one that speaks its API (Forgejo), at an address the user names.</summary>
   Gitea,
}
