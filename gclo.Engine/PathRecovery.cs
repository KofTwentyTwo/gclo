/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>
/// A user-approved plan for materializing a repository whose tree contains
/// Windows-invalid paths: rename some paths, omit others. Applied by
/// <see cref="IGitClient.ApplyRecoveryAsync"/> instead of a normal checkout.
/// </summary>
/// <param name="SegmentRenames">
/// Maps an original repository path (forward-slash separated) to its replacement
/// path. Only the replacement's LAST segment is applied: the entry is renamed in
/// place under its parent's effective (post-rename) location, so renaming a
/// directory and one of its descendants composes. A directory mapping relocates
/// its whole subtree.
/// </param>
/// <param name="SkippedPaths">
/// Repository paths (files or whole directories) to omit from the working tree.
/// </param>
public sealed record PathRecovery(
    IReadOnlyDictionary<string, string> SegmentRenames,
    IReadOnlySet<string> SkippedPaths)
{
   /// <summary>
   /// Where <paramref name="repoPath"/> ends up in the working tree once this
   /// recovery is applied: each part of the path takes the last segment of its own
   /// rename, if it has one, under the place its parent folder ended up. Null when the
   /// path, or a folder above it, is skipped. This is the one definition of how
   /// renames compose; anything that needs to know a destination before the tree is
   /// written (a pre-flight check, an automatic plan) asks here.
   /// </summary>
   public string? EffectivePathOf(string repoPath)
   {
      ArgumentException.ThrowIfNullOrEmpty(repoPath);

      string[] segments = repoPath.Split('/');
      string original = "";
      string effective = "";
      for(int i = 0; i < segments.Length; i++)
      {
         original = i == 0 ? segments[i] : $"{original}/{segments[i]}";
         if(SkippedPaths.Contains(original))
         {
            return null;
         }

         string name = SegmentRenames.TryGetValue(original, out string? mapped)
             ? mapped[(mapped.LastIndexOf('/') + 1)..]
             : segments[i];
         effective = i == 0 ? name : $"{effective}/{name}";
      }

      return effective;
   }
}
