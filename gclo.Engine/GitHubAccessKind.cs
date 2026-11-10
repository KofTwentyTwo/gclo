/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>Why GitHub refused a request, in the terms a caller can act on.</summary>
public enum GitHubAccessKind
{
   /// <summary>The token was rejected (401): expired, revoked, or malformed.</summary>
   Unauthorized,

   /// <summary>The token is valid but may not see the target (an ungranted organization, missing SSO).</summary>
   Forbidden,

   /// <summary>A primary or secondary rate limit, or temporary abuse/login throttling: retry later.</summary>
   RateLimited,

   /// <summary>No organization or user account with that login is visible to the token.</summary>
   NotFound,

   /// <summary>
   /// GitHub could not be reached, did not answer in time, or failed on its side
   /// (a network error, a timeout, HTTP 5xx): nothing is wrong with the request, retry later.
   /// </summary>
   Unavailable,
}
