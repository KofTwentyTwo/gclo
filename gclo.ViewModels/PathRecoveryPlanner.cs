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
/// <remarks>
/// The pre-flight sees the listed paths only, not the rest of the tree. A new name
/// that collides with a path that was valid all along (and so is not listed) is
/// caught by the engine, not here.
/// </remarks>
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
   /// (every listed path is invalid by definition); a replacement may change a path's
   /// name but not its folder; and the places the renamed paths end up go through the
   /// same Windows rules as a checkout would, including two rows renamed onto one
   /// destination. What is validated is the destination, not the replacement as typed:
   /// a renamed entry inside a renamed folder is written with its folder's ORIGINAL
   /// name in front, and lands under the folder's new one (#149).
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
            if(!string.Equals(FolderOf(replacement), FolderOf(choice.RepoPath), StringComparison.Ordinal))
            {
               // Only the last segment is applied; saying nothing would let the user
               // believe the entry moves to the folder they typed.
               problems.Add($"'{replacement}' would move '{choice.RepoPath}' to another folder: only its name can be changed.");
            }
         }
         else
         {
            problems.Add($"'{choice.RepoPath}' is still invalid: rename it or mark it Skip.");
         }
      }

      var recovery = new PathRecovery(renames, skipped);
      List<Placement> placements = Place(recovery);
      foreach((string destination, string reason) in ProblemsOf(placements))
      {
         problems.Add($"'{destination}' cannot be used: {reason}{Origins(placements, destination)}.");
      }

      return new Plan(recovery, problems);
   }



   /// <summary>
   /// The recovery an unattended run applies (<c>--sanitize-paths</c>): every path with
   /// a suggested name is renamed to it, every path without one is skipped, and then
   /// whatever the renames cannot place after all (two paths given the same name, a
   /// name that is also a folder's) is skipped as well, until the listed paths no
   /// longer clash with each other. Two folders given the same name are both skipped,
   /// never merged into one. The result never fails the pre-flight that
   /// <see cref="Build"/> runs (#149).
   /// </summary>
   public static PathRecovery FromSuggestions(IReadOnlyList<InvalidPathInfo> paths)
   {
      ArgumentNullException.ThrowIfNull(paths);

      // Skip wins: a path that also has a suggestion-less problem is not materialized.
      var skipped = new HashSet<string>(
          paths.Where(p => string.IsNullOrEmpty(p.SuggestedName)).Select(p => p.RepoPath),
          StringComparer.Ordinal);

      var renames = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach(InvalidPathInfo info in paths)
      {
         if(info.SuggestedName is not { Length: > 0 } suggestion || skipped.Contains(info.RepoPath))
         {
            continue;
         }

         // The suggestion replaces the last segment: the validator reports each
         // offending segment as a repo path ending in that segment.
         renames[info.RepoPath] = FolderOf(info.RepoPath) + suggestion;
      }

      while(true)
      {
         var recovery = new PathRecovery(renames, skipped);
         List<Placement> placements = Place(recovery);
         var unusable = new HashSet<string>(ProblemsOf(placements).Select(p => p.Destination), StringComparer.Ordinal);
         if(unusable.Count == 0)
         {
            return recovery;
         }

         // Every reported destination is where a placement ends up, or a folder above
         // one, so each round turns at least one rename into a skip.
         foreach(Placement placement in placements)
         {
            if(IsAtOrUnder(placement.Effective, unusable))
            {
               renames.Remove(placement.Original);
               skipped.Add(placement.Original);
            }
         }
      }
   }



   /// <summary>A renamed path and where it ends up.</summary>
   /// <param name="Original">The repository path that is renamed.</param>
   /// <param name="Effective">Where it lands (see <see cref="PathRecovery.EffectivePathOf"/>).</param>
   /// <param name="HoldsRenamedEntries">True when another renamed path lies inside this one, which makes it a folder for certain.</param>
   private sealed record Placement(string Original, string Effective, bool HoldsRenamedEntries);



   /// <summary>
   /// Every renamed path that is written at all (not under a skipped folder), with its
   /// destination, and whether it is known to be a folder: one that holds another
   /// renamed path. Decided by the ORIGINAL paths, which are the tree's; two unrelated
   /// originals whose destinations happen to nest are a clash, not a folder and its
   /// content.
   /// </summary>
   private static List<Placement> Place(PathRecovery recovery)
   {
      var placed = new List<(string Original, string Effective)>();
      var foldersOfPlaced = new HashSet<string>(StringComparer.Ordinal);
      foreach(string original in recovery.SegmentRenames.Keys)
      {
         if(recovery.EffectivePathOf(original) is not { } effective)
         {
            continue;
         }

         placed.Add((original, effective));
         for(int slash = original.LastIndexOf('/'); slash > 0; slash = original.LastIndexOf('/', slash - 1))
         {
            foldersOfPlaced.Add(original[..slash]);
         }
      }

      return placed.Select(p => new Placement(p.Original, p.Effective, foldersOfPlaced.Contains(p.Original))).ToList();
   }



   /// <summary>
   /// The destinations that cannot be used, each with the reason. Destinations of
   /// entries go through the Windows rules together, which also finds two of them at
   /// one place and an entry where a folder is needed. A folder that holds renamed
   /// entries is part of their destinations already, and is checked for the one thing
   /// that would not show there: another renamed path, folder or not, given its name.
   /// </summary>
   private static List<(string Destination, string Reason)> ProblemsOf(List<Placement> placements)
   {
      var problems = new List<(string Destination, string Reason)>();
      var reported = new HashSet<string>(StringComparer.Ordinal);
      foreach(InvalidPathInfo invalid in WindowsPathValidator.ValidatePaths(placements.Where(p => !p.HoldsRenamedEntries).Select(p => p.Effective)))
      {
         if(reported.Add(invalid.RepoPath))
         {
            problems.Add((invalid.RepoPath, invalid.Reason));
         }
      }

      foreach(IGrouping<string, Placement> sameName in placements.GroupBy(p => p.Effective, StringComparer.OrdinalIgnoreCase))
      {
         if(sameName.Count() > 1 && sameName.Any(p => p.HoldsRenamedEntries))
         {
            foreach(string destination in sameName.Select(p => p.Effective).Distinct(StringComparer.Ordinal))
            {
               if(reported.Add(destination))
               {
                  problems.Add((destination, "more than one path maps to this destination"));
               }
            }
         }
      }

      return problems;
   }



   /// <summary>The rows behind a destination, for a message: the user sees originals, not where they land.</summary>
   private static string Origins(List<Placement> placements, string destination)
   {
      var originals = placements
          .Where(p => string.Equals(p.Effective, destination, StringComparison.Ordinal))
          .Select(p => p.Original)
          .Where(original => !string.Equals(original, destination, StringComparison.Ordinal))
          .Order(StringComparer.Ordinal)
          .ToList();
      return originals.Count == 0 ? "" : $" (from {string.Join(", ", originals.Select(o => $"'{o}'"))})";
   }



   /// <summary>True when <paramref name="path"/> is one of <paramref name="paths"/> or lies under one of them.</summary>
   private static bool IsAtOrUnder(string path, HashSet<string> paths)
   {
      for(int end = path.Length; end > 0; end = path.LastIndexOf('/', end - 1))
      {
         if(paths.Contains(path[..end]))
         {
            return true;
         }
      }

      return false;
   }



   /// <summary>The folder part of a repository path, with its trailing slash; empty at the root.</summary>
   private static string FolderOf(string repoPath)
       => repoPath[..(repoPath.LastIndexOf('/') + 1)];
}
