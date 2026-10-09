/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Cli;


/// <summary>
/// A command-line usage error. The message is printed to stderr followed by a
/// help hint, and the process exits with code 2.
/// </summary>
internal sealed class CliUsageException : Exception
{
   public CliUsageException(string message) : base(message)
   {
   }
}
