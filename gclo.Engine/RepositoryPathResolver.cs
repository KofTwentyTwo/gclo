/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>
/// Maps a repository name onto its folder under a sync target root, and refuses any
/// name that is not exactly one safe path segment. <see cref="OrgSyncEngine"/> runs
/// every name through this before it creates a directory or starts a git operation,
/// because the public sync overload accepts caller-supplied descriptors: a name such
/// as <c>..\other</c> or <c>C:\Windows</c> must fail the call, not clone outside the
/// root the caller asked for.
/// </summary>
public static class RepositoryPathResolver
{
   private static readonly char[] s_separatorChars = ['/', '\\'];

   private static readonly char[] s_invalidNameChars = Path.GetInvalidFileNameChars();

   // Windows paths compare case-insensitively; everywhere else the file system is
   // (by default) case-sensitive and so is the containment check.
   private static readonly StringComparison s_pathComparison =
       OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;



   /// <summary>
   /// Returns the normalized absolute folder for <paramref name="repoName"/> under
   /// <paramref name="targetRoot"/>, after proving the name is a single safe segment
   /// and the resolved folder is strictly inside the root.
   /// </summary>
   /// <exception cref="ArgumentException">
   /// <paramref name="targetRoot"/> is blank, or <paramref name="repoName"/> is not a
   /// valid leaf folder name, or the resolved path escapes the root.
   /// </exception>
   public static string Resolve(string targetRoot, string repoName)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);

      if(!TryValidateName(repoName, out string? reason))
      {
         throw new ArgumentException(
             $"Repository name '{repoName}' cannot be used as a folder name: {reason}.", nameof(repoName));
      }

      // Normalize both sides the same way, then require the candidate to sit one
      // level below the root. GetFullPath collapses '.'/'..' segments and, on
      // Windows, trims trailing dots and spaces — so a name that survived the
      // textual checks but still lands on or outside the root is caught here.
      string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetRoot)) + Path.DirectorySeparatorChar;
      string candidate = Path.GetFullPath(Path.Combine(root, repoName));

      if(!candidate.StartsWith(root, s_pathComparison) || candidate.Length <= root.Length)
      {
         throw new ArgumentException(
             $"Repository name '{repoName}' resolves to '{candidate}', which is not inside the target root '{root}'.",
             nameof(repoName));
      }

      return candidate;
   }



   /// <summary>
   /// True when <paramref name="repoName"/> is a single, safe folder-name segment:
   /// not blank, not <c>.</c> or <c>..</c>, no directory separators (so never rooted
   /// or UNC), no drive or stream colon, and no characters a file system rejects in a name.
   /// </summary>
   /// <param name="repoName">The candidate name.</param>
   /// <param name="reason">Why the name was rejected; null when it is valid.</param>
   public static bool TryValidateName(string? repoName, out string? reason)
   {
      if(string.IsNullOrWhiteSpace(repoName))
      {
         reason = "it is empty";
         return false;
      }

      if(repoName is "." or "..")
      {
         reason = "it refers to the current or parent directory";
         return false;
      }

      if(repoName.IndexOfAny(s_separatorChars) >= 0)
      {
         reason = "it contains a directory separator";
         return false;
      }

      // ':' is a drive or alternate-data-stream delimiter on Windows ('C:' and
      // 'C:repo' are drive-relative paths). Reject it on every platform so a name's
      // validity does not depend on where it is checked. With separators and the
      // colon gone, no remaining string is rooted on any supported platform.
      if(repoName.Contains(':'))
      {
         reason = "it contains ':'";
         return false;
      }

      if(repoName.IndexOfAny(s_invalidNameChars) >= 0 || repoName.Any(char.IsControl))
      {
         reason = "it contains characters that are not allowed in a folder name";
         return false;
      }

      reason = null;
      return true;
   }
}
