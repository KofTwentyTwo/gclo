/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.Engine;


namespace gclo.Cli;


/// <summary>
/// A fatal runtime error (missing token, rejected token, organization not found,
/// unusable target folder). The message is printed to stderr and the process
/// exits with <see cref="ExitCode"/> — 2 unless the failure is an auth (3) or
/// transient (4) refusal from GitHub.
/// </summary>
internal sealed class CliErrorException : Exception
{
   public CliErrorException(string message, Exception? inner = null, int exitCode = ExitCodes.Fatal)
       : base(message, inner)
   {
      ExitCode = exitCode;
   }



   /// <summary>The process exit code this error maps to.</summary>
   public int ExitCode { get; }



   /// <summary>
   /// Wraps an engine failure: the listers translate GitHub refusals into
   /// <see cref="GitHubAccessException"/>, whose kind picks the exit code; any other
   /// <see cref="InvalidOperationException"/> stays fatal (2).
   /// </summary>
   public static CliErrorException FromEngine(InvalidOperationException ex)
       => new(ex.Message, ex, ex is GitHubAccessException access ? ExitCodes.ForAccess(access.Kind) : ExitCodes.Fatal);
}
