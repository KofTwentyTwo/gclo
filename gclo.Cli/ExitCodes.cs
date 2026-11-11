/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.Engine;


namespace gclo.Cli;


/// <summary>
/// The process exit codes. 0-2 are the original contract; 3 and 4 split what used
/// to be 2 so a scheduler can tell "fix the invocation or token" from "transient,
/// retry later" without parsing stderr; 70 (BSD EX_SOFTWARE) marks a bug in gclo
/// itself rather than a caller mistake.
/// </summary>
internal static class ExitCodes
{
   /// <summary>Every repository synced (or the listing completed).</summary>
   public const int Success = 0;

   /// <summary>The run completed, but some repositories failed or it was canceled.</summary>
   public const int Partial = 1;

   /// <summary>Fatal: bad arguments, missing token, unknown organization or account, unusable target.</summary>
   public const int Fatal = 2;

   /// <summary>GitHub rejected the token or refused access (401/403): fix the token; never retry as is.</summary>
   public const int Auth = 3;

   /// <summary>
   /// Not the caller's doing: a rate limit or temporary throttle, a network failure,
   /// a timeout, or an error on the server's side (GitHub, or a Gitea server). Retry later.
   /// </summary>
   public const int Transient = 4;

   /// <summary>An unexpected exception inside gclo; details are in the activity log.</summary>
   public const int Unexpected = 70;



   /// <summary>The exit code for an engine access refusal of the given kind.</summary>
   public static int ForAccess(ForgeAccessKind kind) => kind switch
   {
      ForgeAccessKind.Unauthorized or ForgeAccessKind.Forbidden => Auth,
      ForgeAccessKind.RateLimited or ForgeAccessKind.Unavailable => Transient,
      _ => Fatal,
   };
}
