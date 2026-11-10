/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.ViewModels;
using static gclo.Engine.Tests.GitTestHelpers;


namespace gclo.Engine.Tests;


/// <summary>
/// The plan <see cref="PathRecoveryPlanner.FromSuggestions"/> builds from the paths a
/// clone reports is one the real engine accepts and applies (#149). Real repositories
/// on the local file system, like <see cref="PathRecoveryTests"/>.
/// </summary>
public sealed class SuggestedRecoveryAgainstEngineTests : IDisposable
{
   private const string Token = "test-token";

   private readonly string _root =
       Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

   private readonly LibGit2GitClient _client = new();



   public SuggestedRecoveryAgainstEngineTests()
   {
      Directory.CreateDirectory(_root);
   }



   public void Dispose() => TryDeleteDirectory(_root);



   [Fact]
   public async Task AnInvalidFolderWithAnInvalidEntryInside_BothRenamed_UnderTheFoldersNewName()
   {
      string target = await SanitizeAsync(("logs/12:00/run:1.log", "run"), ("logs/12:00/ok.txt", "ok"), ("keep.txt", "keep"));

      Assert.Equal("run", File.ReadAllText(Path.Combine(target, "logs", "12_00", "run_1.log")));
      Assert.Equal("ok", File.ReadAllText(Path.Combine(target, "logs", "12_00", "ok.txt")));
      Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
   }



   [Fact]
   public async Task TwoFilesGivenTheSameName_AreBothLeftOut_AndTheRestIsWritten()
   {
      string target = await SanitizeAsync(("a:b", "one"), ("a?b", "two"), ("keep.txt", "keep"));

      Assert.False(File.Exists(Path.Combine(target, "a_b")));
      Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
   }



   [Fact]
   public async Task TwoFoldersGivenTheSameName_AreBothLeftOut_NeverMergedIntoOne()
   {
      string target = await SanitizeAsync(("a:b/f1.txt", "one"), ("a?b/f2.txt", "two"), ("keep.txt", "keep"));

      Assert.False(Directory.Exists(Path.Combine(target, "a_b")));
      Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
   }



   [Fact]
   public async Task TwoFoldersGivenTheSameName_AreLeftOut_AlsoWhenTheyHoldInvalidEntriesOfTheirOwn()
   {
      // Whether two subtrees are merged or left out must not depend on whether one of
      // them happens to contain another invalid name.
      string target = await SanitizeAsync(("a:b/x:1.txt", "one"), ("a?b/y:1.txt", "two"), ("keep.txt", "keep"));

      Assert.False(Directory.Exists(Path.Combine(target, "a_b")));
      Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
   }



   [Fact]
   public async Task AFileWhoseNewNameIsAlsoAFolderThatGetsARenamedEntry_IsLeftOut_AndTheFolderIsWritten()
   {
      string target = await SanitizeAsync(("a:b", "file"), ("a_b/c:d.txt", "inside"), ("a_b/ok.txt", "ok"));

      Assert.Equal("ok", File.ReadAllText(Path.Combine(target, "a_b", "ok.txt")));
      Assert.True(Directory.Exists(Path.Combine(target, "a_b")));
   }



   [Fact]
   public async Task AFolderAndAFileGivenTheSameName_AreBothLeftOut()
   {
      string target = await SanitizeAsync(("a:b/x:1.txt", "inside"), ("a:b/ok.txt", "ok"), ("a?b", "file"), ("keep.txt", "keep"));

      Assert.False(File.Exists(Path.Combine(target, "a_b")));
      Assert.False(Directory.Exists(Path.Combine(target, "a_b")));
      Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
   }



   /// <summary>
   /// Clones a forged repository (which reports its invalid paths), builds the
   /// automatic plan from that report, and applies it; returns the working directory.
   /// </summary>
   private async Task<string> SanitizeAsync(params (string Name, string Content)[] entries)
   {
      string source = CreateForgedRepo(_root, entries);
      string target = Path.Combine(_root, "clone-" + Guid.NewGuid().ToString("N")[..8]);
      InvalidRepositoryPathsException reported = await Assert.ThrowsAsync<InvalidRepositoryPathsException>(
          () => _client.CloneAsync(source, target, Token, null, CancellationToken.None));

      PathRecovery recovery = PathRecoveryPlanner.FromSuggestions(reported.Paths);
      await _client.ApplyRecoveryAsync(target, recovery, CancellationToken.None);

      return target;
   }
}
