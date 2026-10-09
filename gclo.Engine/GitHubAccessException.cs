/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>
/// A GitHub API refusal translated into an actionable message. Derives from
/// <see cref="InvalidOperationException"/> so every existing catch keeps working;
/// <see cref="Kind"/> lets a caller tell "fix your token" from "wait and retry"
/// without parsing the message — the CLI maps it to distinct exit codes.
/// </summary>
public sealed class GitHubAccessException : InvalidOperationException
{
   /// <summary>The category of refusal.</summary>
   public GitHubAccessKind Kind { get; }



   /// <summary>Creates the exception with its category, user-facing message, and the Octokit cause.</summary>
   public GitHubAccessException(GitHubAccessKind kind, string message, Exception? innerException = null)
       : base(message, innerException)
   {
      Kind = kind;
   }
}
