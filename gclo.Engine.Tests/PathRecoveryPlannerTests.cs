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



   // ---------------------------------------------------------------- destinations, not replacements as typed (#149)

   [Fact]
   public void Build_RenamedEntryInsideARenamedFolder_IsSound_AlthoughItsReplacementStillShowsTheOldFolderName()
   {
      // The dialog writes each replacement with the row's ORIGINAL parent in front;
      // the engine applies only the last segment, under the folder's new name.
      Plan plan = Build(
      [
          new Choice("logs/12:00", "logs/12_00", Skip: false),
            new Choice("logs/12:00/run:1.log", "logs/12:00/run_1.log", Skip: false),
        ]);

      Assert.Empty(plan.Problems);
      Assert.Equal("logs/12_00/run_1.log", plan.Recovery.EffectivePathOf("logs/12:00/run:1.log"));
   }



   [Fact]
   public void Build_RenamedEntryInsideARenamedFolder_IsStillCheckedByItsOwnNewName()
   {
      Plan plan = Build(
      [
          new Choice("logs/12:00", "logs/12_00", Skip: false),
            new Choice("logs/12:00/run:1.log", "logs/12:00/run?1.log", Skip: false),
        ]);

      string problem = Assert.Single(plan.Problems);
      Assert.Contains("'logs/12_00/run?1.log' cannot be used", problem);
   }



   [Fact]
   public void Build_TwoRowsThatEndUpAtTheSamePlace_AreAProblem_EvenWhenTheirReplacementsDiffer()
   {
      // Different folders as typed, the same folder once the folder's rename applies.
      Plan plan = Build(
      [
          new Choice("a:b", "a_b", Skip: false),
            new Choice("a:b/x:1.txt", "a:b/x_1.txt", Skip: false),
            new Choice("a:b/x?1.txt", "a:b/x_1.txt", Skip: false),
        ]);

      string problem = Assert.Single(plan.Problems);
      Assert.Contains("'a_b/x_1.txt' cannot be used: more than one path maps to this destination", problem);
   }



   [Fact]
   public void Build_RenamedEntryUnderASkippedFolder_IsNotChecked_BecauseItIsNotWritten()
   {
      Plan plan = Build(
      [
          new Choice("skipped:dir", null, Skip: true),
            new Choice("skipped:dir/still:bad.txt", "skipped:dir/still?bad.txt", Skip: false),
        ]);

      Assert.Empty(plan.Problems);
      Assert.Null(plan.Recovery.EffectivePathOf("skipped:dir/still:bad.txt"));
   }



   [Fact]
   public void Build_AFileRenamedOntoANameThatIsAlsoAFolderOfAnotherRename_IsAProblem()
   {
      // Two unrelated originals: a file that becomes "a_b", and an entry inside the
      // existing folder "a_b". Their destinations nest, which is a clash, not a
      // folder with its content.
      Plan plan = Build(
      [
          new Choice("a:b", "a_b", Skip: false),
            new Choice("a_b/c:d", "a_b/c_d", Skip: false),
        ]);

      string problem = Assert.Single(plan.Problems);
      Assert.Contains("'a_b' cannot be used: maps to both a file and a directory (from 'a:b')", problem);
   }



   [Fact]
   public void Build_AFolderAndAFileRenamedOntoOneName_IsAProblem()
   {
      Plan plan = Build(
      [
          new Choice("a:b", "a_b", Skip: false),
            new Choice("a:b/x:1", "a:b/x_1", Skip: false),
            new Choice("a?b", "a_b", Skip: false),
        ]);

      Assert.Contains(plan.Problems, p => p.StartsWith("'a_b' cannot be used", StringComparison.Ordinal));
   }



   [Fact]
   public void Build_TwoFoldersRenamedOntoOneName_AreAProblem_NamedWithTheRowsTheyComeFrom()
   {
      Plan plan = Build(
      [
          new Choice("a:b", "a_b", Skip: false),
            new Choice("a:b/x:1", "a:b/x_1", Skip: false),
            new Choice("a?b", "a_b", Skip: false),
            new Choice("a?b/y:1", "a?b/y_1", Skip: false),
        ]);

      string problem = Assert.Single(plan.Problems);
      Assert.Equal("'a_b' cannot be used: more than one path maps to this destination (from 'a:b', 'a?b').", problem);
   }



   [Fact]
   public void Build_ThreeRowsAtOnePlace_AreReportedOnce()
   {
      Plan plan = Build(
      [
          new Choice("x:1", "x_1", Skip: false),
            new Choice("x?1", "x_1", Skip: false),
            new Choice("x*1", "x_1", Skip: false),
        ]);

      string problem = Assert.Single(plan.Problems);
      Assert.Contains("(from 'x*1', 'x:1', 'x?1')", problem);
   }



   [Theory]
   [InlineData("docs/bad:name.txt", "elsewhere/bad_name.txt")]
   [InlineData("docs/bad:name.txt", "docs/sub/bad_name.txt")]
   [InlineData("docs/bad:name.txt", "bad_name.txt")]
   [InlineData("bad:name.txt", "docs/bad_name.txt")]
   public void Build_AReplacementInAnotherFolder_IsAProblem_BecauseOnlyTheNameIsApplied(string path, string replacement)
   {
      Plan plan = Build([new Choice(path, replacement, Skip: false)]);

      string problem = Assert.Single(plan.Problems);
      Assert.Contains($"'{replacement}' would move '{path}' to another folder", problem);
   }



   // ---------------------------------------------------------------- the automatic plan (#149)

   [Fact]
   public void FromSuggestions_RenamesWhatHasASuggestion_AndSkipsWhatHasNone()
   {
      PathRecovery recovery = PathRecoveryPlanner.FromSuggestions(
      [
          new InvalidPathInfo("bad:name.txt", "invalid", "bad_name.txt"),
            new InvalidPathInfo("dir/CON", "reserved", "CON_"),
            new InvalidPathInfo("README.md", "differs only by case from 'readme.md'", null),
        ]);

      Assert.Equal("bad_name.txt", recovery.SegmentRenames["bad:name.txt"]);
      Assert.Equal("dir/CON_", recovery.SegmentRenames["dir/CON"]);
      Assert.Equal(["README.md"], recovery.SkippedPaths, StringComparer.Ordinal);
   }



   [Fact]
   public void FromSuggestions_APathThatAlsoHasAProblemWithoutASuggestion_IsSkipped()
   {
      PathRecovery recovery = PathRecoveryPlanner.FromSuggestions(
      [
          new InvalidPathInfo("x:y.txt", "invalid", "x_y.txt"),
            new InvalidPathInfo("x:y.txt", "differs only by case from 'X:Y.txt'", null),
        ]);

      Assert.Empty(recovery.SegmentRenames);
      Assert.Equal(["x:y.txt"], recovery.SkippedPaths, StringComparer.Ordinal);
   }



   [Fact]
   public void FromSuggestions_TwoPathsGivenTheSameName_AreSkipped_RatherThanFailingTheApply()
   {
      PathRecovery recovery = PathRecoveryPlanner.FromSuggestions(
      [
          new InvalidPathInfo("a:b", "invalid", "a_b"),
            new InvalidPathInfo("a?b", "invalid", "a_b"),
            new InvalidPathInfo("fine:name.txt", "invalid", "fine_name.txt"),
        ]);

      Assert.Equal("fine_name.txt", Assert.Single(recovery.SegmentRenames).Value);
      Assert.Equal(new[] { "a:b", "a?b" }, recovery.SkippedPaths.Order(StringComparer.Ordinal), StringComparer.Ordinal);
      // What comes out always passes the pre-flight the dialog runs.
      Assert.Empty(Build(recovery.SegmentRenames.Select(r => new Choice(r.Key, r.Value, Skip: false)).ToList()).Problems);
   }



   [Fact]
   public void FromSuggestions_AnInvalidFolderAndAnInvalidEntryInsideIt_AreBothRenamed_AndCompose()
   {
      PathRecovery recovery = PathRecoveryPlanner.FromSuggestions(
      [
          new InvalidPathInfo("logs/12:00", "invalid", "12_00"),
            new InvalidPathInfo("logs/12:00/run:1.log", "invalid", "run_1.log"),
        ]);

      Assert.Empty(recovery.SkippedPaths);
      Assert.Equal("logs/12_00/run_1.log", recovery.EffectivePathOf("logs/12:00/run:1.log"));
   }



   [Fact]
   public void FromSuggestions_TwoEntriesCollidingInsideARenamedFolder_AreSkipped_AndTheFolderKeepsItsRename()
   {
      PathRecovery recovery = PathRecoveryPlanner.FromSuggestions(
      [
          new InvalidPathInfo("a:b", "invalid", "a_b"),
            new InvalidPathInfo("a:b/x:1", "invalid", "x_1"),
            new InvalidPathInfo("a:b/x?1", "invalid", "x_1"),
        ]);

      Assert.Equal("a_b", Assert.Single(recovery.SegmentRenames).Value);
      Assert.Equal(new[] { "a:b/x:1", "a:b/x?1" }, recovery.SkippedPaths.Order(StringComparer.Ordinal), StringComparer.Ordinal);
   }



   [Fact]
   public void FromSuggestions_TwoFoldersGivenTheSameName_AreBothSkipped_WithEverythingInThem()
   {
      PathRecovery recovery = PathRecoveryPlanner.FromSuggestions(
      [
          new InvalidPathInfo("a:b", "invalid", "a_b"),
            new InvalidPathInfo("a:b/x:1", "invalid", "x_1"),
            new InvalidPathInfo("a?b", "invalid", "a_b"),
            new InvalidPathInfo("a?b/y:1", "invalid", "y_1"),
            new InvalidPathInfo("fine:name.txt", "invalid", "fine_name.txt"),
        ]);

      Assert.Equal("fine_name.txt", Assert.Single(recovery.SegmentRenames).Value);
      Assert.Contains("a:b", recovery.SkippedPaths);
      Assert.Contains("a?b", recovery.SkippedPaths);
      Assert.Null(recovery.EffectivePathOf("a:b/x:1"));
      Assert.Null(recovery.EffectivePathOf("a?b/y:1"));
   }



   [Fact]
   public void FromSuggestions_AnEntryWhoseFolderIsInvalidAndNotListed_IsSkipped_AndThePlanIsFinished()
   {
      // The problem is on the folder above the destination, not on the destination
      // itself; the entry is what gets skipped, and the loop ends.
      PathRecovery recovery = PathRecoveryPlanner.FromSuggestions(
          [new InvalidPathInfo("logs/12:00/run:1.log", "invalid", "run_1.log")]);

      Assert.Empty(recovery.SegmentRenames);
      Assert.Equal(["logs/12:00/run:1.log"], recovery.SkippedPaths, StringComparer.Ordinal);
   }



   [Fact]
   public void FromSuggestions_TwoNamesThatWouldDifferOnlyByCase_KeepTheFirstAndSkipTheSecond()
   {
      PathRecovery recovery = PathRecoveryPlanner.FromSuggestions(
      [
          new InvalidPathInfo("a:B", "invalid", "a_B"),
            new InvalidPathInfo("a:b", "invalid", "a_b"),
        ]);

      Assert.Equal("a_B", Assert.Single(recovery.SegmentRenames).Value);
      Assert.Equal(["a:b"], recovery.SkippedPaths, StringComparer.Ordinal);
   }



   [Fact]
   public void FromSuggestions_AndBuild_StayFast_ForARepositoryWithTensOfThousandsOfInvalidPaths()
   {
      // Timestamps in file names are the usual way to get this many. The work per path
      // must not grow with the number of paths (a pairwise comparison took a minute).
      var paths = Enumerable.Range(0, 50_000)
          .Select(i => new InvalidPathInfo($"data/2026-01-01T00:00:{i}.json", "invalid", $"2026-01-01T00_00_{i}.json"))
          .ToList();
      var clock = System.Diagnostics.Stopwatch.StartNew();

      PathRecovery recovery = PathRecoveryPlanner.FromSuggestions(paths);
      Plan plan = Build(recovery.SegmentRenames.Select(r => new Choice(r.Key, r.Value, Skip: false)).ToList());

      Assert.Equal(50_000, recovery.SegmentRenames.Count);
      Assert.Empty(plan.Problems);
      Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
   }



   [Fact]
   public void FromSuggestions_Null_Throws()
   {
      Assert.Throws<ArgumentNullException>(() => PathRecoveryPlanner.FromSuggestions(null!));
   }
}
