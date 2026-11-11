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
   /// Wraps an engine failure: the listers of every forge translate refusals into
   /// <see cref="ForgeAccessException"/>, whose kind picks the exit code; any other
   /// <see cref="InvalidOperationException"/> stays fatal (2).
   /// </summary>
   public static CliErrorException FromEngine(InvalidOperationException ex)
       => new(ex.Message, ex, ex is ForgeAccessException access ? ExitCodes.ForAccess(access.Kind) : ExitCodes.Fatal);



   /// <summary>
   /// True for a failure of the accounts file or of the credential store: not a bug
   /// in gclo and not a usage mistake, but something on this machine that the
   /// message names (a file that cannot be written, an entry that cannot be read).
   /// </summary>
   public static bool IsStorageFailure(Exception ex)
       => ex is IOException or UnauthorizedAccessException or gclo.ViewModels.AccountConsistencyException
           or System.ComponentModel.Win32Exception;



   /// <summary>
   /// The error a storage failure ends a command with: fatal (2), or "retry later"
   /// (4) when another gclo process was changing the accounts at that moment. A
   /// failure of the credential store carries only a number; what Windows says that
   /// number means is added.
   /// </summary>
   public static CliErrorException FromStorage(Exception ex)
   {
      string message = ex is System.ComponentModel.Win32Exception native
          ? $"{ex.Message} ({new System.ComponentModel.Win32Exception(native.NativeErrorCode).Message})"
          : ex.Message;
      return new CliErrorException(
          message, ex, ex is gclo.ViewModels.AccountsFileBusyException ? ExitCodes.Transient : ExitCodes.Fatal);
   }
}
