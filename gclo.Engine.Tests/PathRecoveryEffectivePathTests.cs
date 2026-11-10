/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine.Tests;


/// <summary>
/// <see cref="PathRecovery.EffectivePathOf"/> is the one definition of where a path
/// ends up once renames and skips are applied (#149).
/// </summary>
public sealed class PathRecoveryEffectivePathTests
{
   private static PathRecovery Recovery((string From, string To)[]? renames = null, string[]? skips = null)
       => new(
           (renames ?? []).ToDictionary(r => r.From, r => r.To, StringComparer.Ordinal),
           new HashSet<string>(skips ?? [], StringComparer.Ordinal));



   [Theory]
   [InlineData("readme.txt")]
   [InlineData("src/app/main.cs")]
   public void APathNothingApplyTo_StaysWhereItIs(string path)
   {
      Assert.Equal(path, Recovery(renames: [("other:file.txt", "other_file.txt")], skips: ["gone"]).EffectivePathOf(path));
   }



   [Fact]
   public void ARenamedFile_TakesTheLastSegmentOfItsReplacement_UnderItsOwnFolder()
   {
      PathRecovery recovery = Recovery(renames: [("docs/bad:name.txt", "docs/bad_name.txt")]);

      Assert.Equal("docs/bad_name.txt", recovery.EffectivePathOf("docs/bad:name.txt"));
   }



   [Fact]
   public void OnlyTheLastSegmentOfAReplacementCounts_WhateverFolderItIsWrittenWith()
   {
      // A replacement cannot move an entry to another folder.
      PathRecovery recovery = Recovery(renames: [("docs/bad:name.txt", "somewhere/else/bad_name.txt")]);

      Assert.Equal("docs/bad_name.txt", recovery.EffectivePathOf("docs/bad:name.txt"));
   }



   [Fact]
   public void ARenamedFolder_MovesEverythingUnderIt()
   {
      PathRecovery recovery = Recovery(renames: [("logs/12:00", "logs/12_00")]);

      Assert.Equal("logs/12_00", recovery.EffectivePathOf("logs/12:00"));
      Assert.Equal("logs/12_00/run.log", recovery.EffectivePathOf("logs/12:00/run.log"));
      Assert.Equal("logs/12_00/deep/er/run.log", recovery.EffectivePathOf("logs/12:00/deep/er/run.log"));
      Assert.Equal("logs/other/run.log", recovery.EffectivePathOf("logs/other/run.log"));
   }



   [Fact]
   public void RenamesOfAFolderAndOfAnEntryInsideIt_Compose()
   {
      PathRecovery recovery = Recovery(renames: [("logs/12:00", "logs/12_00"), ("logs/12:00/run:1.log", "logs/12:00/run_1.log")]);

      Assert.Equal("logs/12_00/run_1.log", recovery.EffectivePathOf("logs/12:00/run:1.log"));
   }



   [Fact]
   public void ASkippedPath_AndEverythingUnderASkippedFolder_EndsUpNowhere()
   {
      PathRecovery recovery = Recovery(renames: [("gone/bad:name.txt", "gone/bad_name.txt")], skips: ["gone", "single:file.txt"]);

      Assert.Null(recovery.EffectivePathOf("single:file.txt"));
      Assert.Null(recovery.EffectivePathOf("gone"));
      Assert.Null(recovery.EffectivePathOf("gone/kept.txt"));
      Assert.Null(recovery.EffectivePathOf("gone/bad:name.txt"));
      Assert.Equal("gone-too/kept.txt", recovery.EffectivePathOf("gone-too/kept.txt"));
   }



   [Theory]
   [InlineData(null)]
   [InlineData("")]
   public void NoPath_IsACallerError(string? path)
   {
      Assert.ThrowsAny<ArgumentException>(() => Recovery().EffectivePathOf(path!));
   }
}
