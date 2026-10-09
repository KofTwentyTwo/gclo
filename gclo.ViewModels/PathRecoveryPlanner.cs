/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.Engine;


namespace gclo.ViewModels;


/// <summary>
/// Turns the per-row choices of the path-recovery UI (rename to this, or skip) into a
/// <see cref="PathRecovery"/>, and reports the choices that would make the apply fail
/// anyway — so the dialog can flag them inline instead of closing, failing in the
/// engine, and re-opening with a fresh list (#30). The engine still re-validates the
/// full effective tree on apply; this is the pre-flight for what the user can see.
/// </summary>
public static class PathRecoveryPlanner
{
   /// <summary>One row's choice.</summary>
   /// <param name="RepoPath">The invalid repository path, as reported.</param>
   /// <param name="ReplacementPath">The full replacement path, or null when the name was left as is.</param>
   /// <param name="Skip">True to omit the file or folder from the checkout.</param>
   public sealed record Choice(string RepoPath, string? ReplacementPath, bool Skip);



   /// <summary>The recovery to apply plus the reasons it would still fail, if any.</summary>
   /// <param name="Recovery">Renames and skips, as the engine consumes them.</param>
   /// <param name="Problems">Human-readable problems, one per offending row; empty when the plan is sound.</param>
   public sealed record Plan(PathRecovery Recovery, IReadOnlyList<string> Problems);



   /// <summary>
   /// Builds the plan. A row that is neither skipped nor renamed is always a problem
   /// (every listed path is invalid by definition), and replacement names go through
   /// the same Windows rules as a checkout would, including two rows renamed onto one
   /// destination.
   /// </summary>
   public static Plan Build(IReadOnlyList<Choice> choices)
   {
      ArgumentNullException.ThrowIfNull(choices);

      // Git paths are case-sensitive, so both collections compare ordinally. The same
      // repo path can appear in two rows (e.g. an invalid segment that also collides
      // by case), which is why renames assign via the indexer instead of Add.
      var renames = new Dictionary<string, string>(StringComparer.Ordinal);
      var skipped = new HashSet<string>(StringComparer.Ordinal);
      var problems = new List<string>();

      foreach(Choice choice in choices)
      {
         if(choice.Skip)
         {
            skipped.Add(choice.RepoPath);
         }
         else if(choice.ReplacementPath is { } replacement)
         {
            renames[choice.RepoPath] = replacement;
         }
         else
         {
            problems.Add($"'{choice.RepoPath}' is still invalid: rename it or mark it Skip.");
         }
      }

      foreach(InvalidPathInfo stillInvalid in WindowsPathValidator.ValidatePaths(renames.Values))
      {
         problems.Add($"'{stillInvalid.RepoPath}' cannot be used: {stillInvalid.Reason}.");
      }

      return new Plan(new PathRecovery(renames, skipped), problems);
   }
}
