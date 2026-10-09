/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.ViewModels;
using static gclo.ViewModels.PathRecoveryPlanner;


namespace gclo.Engine.Tests;


/// <summary>
/// <see cref="PathRecoveryPlanner"/> is the pre-flight behind the recovery dialog's
/// "Apply and check out": it must build exactly the recovery the engine expects and
/// name every choice that would make the apply fail.
/// </summary>
public sealed class PathRecoveryPlannerTests
{
   [Fact]
   public void Build_RenamesAndSkips_ProduceTheRecovery_WithNoProblems()
   {
      Plan plan = Build(
      [
          new Choice("bad:name.txt", "bad_name.txt", Skip: false),
            new Choice("dir/CON", "dir/CON_", Skip: false),
            new Choice("Readme.md", null, Skip: true),
        ]);

      Assert.Empty(plan.Problems);
      Assert.Equal("bad_name.txt", plan.Recovery.SegmentRenames["bad:name.txt"]);
      Assert.Equal("dir/CON_", plan.Recovery.SegmentRenames["dir/CON"]);
      Assert.Equal(["Readme.md"], plan.Recovery.SkippedPaths, StringComparer.Ordinal);
   }



   [Fact]
   public void Build_UnchangedRowThatIsNotSkipped_IsAProblem()
   {
      Plan plan = Build([new Choice("Readme.md", null, Skip: false)]);

      string problem = Assert.Single(plan.Problems);
      Assert.Contains("'Readme.md' is still invalid", problem);
      Assert.Empty(plan.Recovery.SegmentRenames);
      Assert.Empty(plan.Recovery.SkippedPaths);
   }



   [Fact]
   public void Build_ReplacementThatIsItselfInvalid_IsAProblem()
   {
      Plan plan = Build([new Choice("bad:name.txt", "still:bad.txt", Skip: false)]);

      string problem = Assert.Single(plan.Problems);
      Assert.Contains("'still:bad.txt' cannot be used", problem);
      Assert.Contains("invalid on Windows", problem);
   }



   [Fact]
   public void Build_TwoRowsRenamedOntoOneDestination_IsAProblem()
   {
      Plan plan = Build(
      [
          new Choice("a:b.txt", "a_b.txt", Skip: false),
            new Choice("a?b.txt", "a_b.txt", Skip: false),
        ]);

      string problem = Assert.Single(plan.Problems);
      Assert.Contains("more than one path maps to this destination", problem);
   }



   [Fact]
   public void Build_SamePathInTwoRows_LastRenameWins_AndSkipTrumpsNothing()
   {
      // The validator can list one path twice (invalid segment AND case collision);
      // the dialog shows two rows, and the recovery must still be a single mapping.
      Plan plan = Build(
      [
          new Choice("x:y.txt", "x_y.txt", Skip: false),
            new Choice("x:y.txt", "x-y.txt", Skip: false),
        ]);

      Assert.Empty(plan.Problems);
      Assert.Equal("x-y.txt", Assert.Single(plan.Recovery.SegmentRenames).Value);
   }



   [Fact]
   public void Build_Null_Throws()
   {
      Assert.Throws<ArgumentNullException>(() => Build(null!));
   }
}
