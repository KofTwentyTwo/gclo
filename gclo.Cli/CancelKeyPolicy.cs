/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Cli;


/// <summary>
/// What the first Ctrl+C does. For the commands gclo knows, the key press is held
/// back and the command gets to finish: a sync, a listing, or an organization
/// lookup watches for the request and stops gracefully (the summary still prints);
/// a change to the accounts does not watch, and is simply let through to its end,
/// because it writes to two places (the credential store and the accounts file) and
/// must not be cut off between them. While the process sits in a prompt waiting for
/// a line, nothing has been started yet, and it ends at once; so does anything on a
/// second Ctrl+C.
/// </summary>
internal static class CancelKeyPolicy
{
   /// <summary>
   /// True when the key press is to be held back; false when the process is to end
   /// the way Ctrl+C ends any process.
   /// </summary>
   /// <param name="command">The command being run (the first argument), or null when there is none.</param>
   /// <param name="isReadingInput">Whether the process is waiting for a line on standard input right now.</param>
   /// <param name="alreadyRequested">Whether an earlier Ctrl+C was already held back.</param>
   public static bool HoldsBack(string? command, bool isReadingInput, bool alreadyRequested)
       => !alreadyRequested && !isReadingInput && command is "sync" or "repos" or "orgs" or "accounts";



   /// <summary>What the user is told when the key press was held back for <paramref name="command"/>.</summary>
   public static string Notice(string? command)
       => string.Equals(command, "accounts", StringComparison.Ordinal)
           ? "Finishing the change to the accounts... (press Ctrl+C again to abort immediately)"
           : "Canceling... (press Ctrl+C again to abort immediately)";
}
