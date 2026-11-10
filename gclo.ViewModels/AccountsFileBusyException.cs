/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.ViewModels;


/// <summary>
/// A change to the accounts could not be made because another gclo process kept the
/// accounts file's lock for the whole wait. Nothing was changed, and nothing is
/// wrong: the same change succeeds once the other process has finished. An
/// <see cref="IOException"/>, so everything that already handles one keeps working.
/// </summary>
public sealed class AccountsFileBusyException : IOException
{
   /// <summary>Creates the exception with its message and the sharing violation that caused it.</summary>
   public AccountsFileBusyException(string message, Exception innerException)
       : base(message, innerException)
   {
   }
}
