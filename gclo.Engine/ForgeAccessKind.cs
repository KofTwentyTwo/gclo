/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>
/// Why a request to a forge (GitHub, a Gitea server) failed, in the terms a caller
/// can act on. The members and their values are those of <see cref="GitHubAccessKind"/>.
/// </summary>
public enum ForgeAccessKind
{
   /// <summary>The token was rejected (401): expired, revoked, deleted, or malformed.</summary>
   Unauthorized,

   /// <summary>The token is valid but may not see the target, or lacks a permission the request needs.</summary>
   Forbidden,

   /// <summary>The server is limiting requests from this token: retry later.</summary>
   RateLimited,

   /// <summary>
   /// No organization or account of that name is visible to the token; or, for a
   /// server at an address the user named, nothing that answers like that forge is
   /// at the address.
   /// </summary>
   NotFound,

   /// <summary>
   /// The server could not be reached, did not answer in time, or failed on its side
   /// (a network error, a timeout, HTTP 5xx): nothing is wrong with the request, retry later.
   /// </summary>
   Unavailable,
}
