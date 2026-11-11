/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>
/// A failed GitHub API request (a refusal, or GitHub not answering) translated into an actionable message. Derives from
/// <see cref="InvalidOperationException"/> (through <see cref="ForgeAccessException"/>,
/// the type the listers of every forge throw) so every existing catch keeps working;
/// <see cref="Kind"/> lets a caller tell "fix your token" from "wait and retry"
/// without parsing the message — the CLI maps it to distinct exit codes.
/// </summary>
public sealed class GitHubAccessException : ForgeAccessException
{
   /// <summary>The category of refusal. The same category, as a <see cref="ForgeAccessKind"/>, is on the base type.</summary>
   public new GitHubAccessKind Kind { get; }



   /// <summary>Creates the exception with its category, user-facing message, and the Octokit cause.</summary>
   public GitHubAccessException(GitHubAccessKind kind, string message, Exception? innerException = null)
       : base((ForgeAccessKind)(int)kind, message, innerException)
   {
      Kind = kind;
   }
}
