/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.ViewModels;


/// <summary>
/// The single seam for where gclo keeps its per-user data (settings, accounts,
/// logs). Defaults to %LOCALAPPDATA%\KofTwentyTwo\gclo; the GCLO_DATA_DIR
/// environment variable overrides it so UI tests (and portable setups) can point
/// the whole app at an isolated directory without touching the real profile.
/// Components that accept an explicit directory (test seams) are unaffected —
/// this only feeds defaults.
/// </summary>
/// <remarks>
/// The data root must never be the Velopack install root (%LOCALAPPDATA%\gclo):
/// Setup.exe uninstalls an existing install first and deletes that whole folder,
/// which destroyed the accounts and settings stored there (#91).
/// </remarks>
public static class GcloPaths
{
   /// <summary>
   /// Root directory for gclo's per-user data: GCLO_DATA_DIR when set and
   /// non-empty, otherwise <see cref="DefaultDataRoot"/>. Read on every access so
   /// a value set before launch always wins over any cached default.
   /// </summary>
   public static string DataRoot =>
       Environment.GetEnvironmentVariable("GCLO_DATA_DIR") is { Length: > 0 } dir
           ? dir
           : DefaultDataRoot;



   /// <summary>
   /// %LOCALAPPDATA%\KofTwentyTwo\gclo: the root used when GCLO_DATA_DIR is not
   /// set. Outside the Velopack install root on purpose (see the type remarks).
   /// </summary>
   public static string DefaultDataRoot =>
       Path.Combine(
           Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
           "KofTwentyTwo", "gclo");
}
