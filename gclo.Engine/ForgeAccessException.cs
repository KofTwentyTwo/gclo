/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>
/// A failed request to a forge (a refusal, or the server not answering) translated
/// into an actionable message. Derives from <see cref="InvalidOperationException"/>
/// so every existing catch keeps working; <see cref="Kind"/> lets a caller tell "fix
/// your token" from "wait and retry" without parsing the message. Listers of every
/// forge throw it; the GitHub listers throw the derived <see cref="GitHubAccessException"/>.
/// </summary>
public class ForgeAccessException : InvalidOperationException
{
   /// <summary>The category of failure.</summary>
   public ForgeAccessKind Kind { get; }



   /// <summary>Creates the exception with its category, user-facing message, and the cause.</summary>
   public ForgeAccessException(ForgeAccessKind kind, string message, Exception? innerException = null)
       : base(message, innerException)
   {
      Kind = kind;
   }
}
