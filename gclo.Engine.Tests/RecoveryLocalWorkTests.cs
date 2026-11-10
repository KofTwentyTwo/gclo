/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Text;
using System.Text.Json.Nodes;
using LibGit2Sharp;
using static gclo.Engine.Tests.GitTestHelpers;


namespace gclo.Engine.Tests;


/// <summary>
/// Path recovery writes a working tree by hand, so it is only done to a tree that is
/// gclo's to write, never moves a branch off a commit made in the repository, and
/// finishes what an interrupted apply left (#147). Real repositories on the local
/// file system, like <see cref="PathRecoveryTests"/>.
/// </summary>
public sealed class RecoveryLocalWorkTests : IDisposable
{
   private const string Token = "test-token";

   private const string PendingKey = "gclo.checkoutpending";

   private const string ManagedKey = "gclo.recoverymanaged";

   private readonly string _root =
       Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

   private readonly LibGit2GitClient _client = new();



   public RecoveryLocalWorkTests()
   {
      Directory.CreateDirectory(_root);
   }



   public void Dispose() => TryDeleteDirectory(_root);



   // ---------------------------------------------------------------- a clone that was checked out is never rewritten

   [Theory]
   [InlineData("clean")]
   [InlineData("uncommitted edit")]
   [InlineData("untracked file in the way")]
   [InlineData("local commit")]
   public async Task OrdinaryClone_IsRefused_WhateverItsState_AndNothingInItChanges(string state)
   {
      (string source, string target) = await CloneOrdinaryAsync(("readme.txt", "v1"), ("work.txt", "work"));
      switch(state)
      {
         case "uncommitted edit":
            File.WriteAllText(Path.Combine(target, "work.txt"), "my local edit");
            break;
         case "untracked file in the way":
            File.WriteAllText(Path.Combine(target, "bad_file.txt"), "mine, never committed");
            break;
         case "local commit":
            CommitInWorkingTree(target, "mine.txt", "my work");
            break;
         default:
            break;
      }
      string before = HeadSha(target);
      Dictionary<string, string> filesBefore = ReadAllFiles(target);
      AppendForgedCommit(source, ("bad:file.txt", "bad"), ("readme.txt", "v2"));
      await PullExpectingInvalidPathsAsync(target);

      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.ApplyRecoveryAsync(target, Recovery(renames: [("bad:file.txt", "bad_file.txt")]), CancellationToken.None));

      Assert.Contains("is checked out normally", ex.Message);
      Assert.Contains("move or delete the folder", ex.Message);
      Assert.Contains(target, ex.Message);
      Assert.Equal(before, HeadSha(target));
      Assert.Equal(filesBefore, ReadAllFiles(target));
      Assert.False(File.Exists(RecoveryFilePath(target)));
      Assert.False(IsFlagSet(target, ManagedKey), "a refused apply leaves the repository an ordinary clone");
      Assert.False(IsFlagSet(target, PendingKey));

      // And it stays an ordinary clone to the next pull, which reports the same paths.
      await PullExpectingInvalidPathsAsync(target);
      Assert.Equal(filesBefore, ReadAllFiles(target));
   }



   [Fact]
   public async Task OrdinaryClone_WithAnUnreadableRecoveryFileInIt_IsNotTurnedIntoAManagedRepository()
   {
      (_, string target) = await CloneOrdinaryAsync(("readme.txt", "v1"), ("work.txt", "work"));
      File.WriteAllText(RecoveryFilePath(target), "{ this is not a recovery file");

      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.PullAsync(target, Token, CancellationToken.None));
      Assert.Contains("is corrupt", ex.Message);
      Assert.False(IsFlagSet(target, ManagedKey), "a file that could not be read proves nothing about the repository");

      // The message says to delete it. After that the clone is an ordinary clone again,
      // and a pull does what a pull does: it leaves local edits alone.
      File.Delete(RecoveryFilePath(target));
      File.WriteAllText(Path.Combine(target, "work.txt"), "my local edit");
      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.Equal("my local edit", File.ReadAllText(Path.Combine(target, "work.txt")));
      Assert.False(IsFlagSet(target, ManagedKey));
   }



   [Fact]
   public async Task EmptyClone_WhoseOriginGetsItsFirstCommitWithAnInvalidPath_CanBeResolved()
   {
      string source = Path.Combine(_root, "empty-source");
      Repository.Init(source);
      string target = Path.Combine(_root, "empty-clone");
      await _client.CloneAsync(source, target, Token, null, CancellationToken.None);
      string tip = FirstForgedCommit(source, ("bad:file.txt", "bad"), ("keep.txt", "keep"));
      await PullExpectingInvalidPathsAsync(target);

      // Nothing was ever checked out here, so the tree is gclo's to write.
      await _client.ApplyRecoveryAsync(target, Recovery(renames: [("bad:file.txt", "bad_file.txt")]), CancellationToken.None);

      Assert.Equal(tip, HeadSha(target));
      Assert.Equal("bad", File.ReadAllText(Path.Combine(target, "bad_file.txt")));
      Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
      // The branch tracks origin, so later pulls follow it.
      using(var repo = new Repository(target))
      {
         Assert.Equal("refs/remotes/origin/" + repo.Head.FriendlyName, repo.Head.TrackedBranch?.CanonicalName);
      }
      string next = AppendForgedCommit(source, ("more.txt", "m"));
      await _client.PullAsync(target, Token, CancellationToken.None);
      Assert.Equal(next, HeadSha(target));
      Assert.Equal("m", File.ReadAllText(Path.Combine(target, "more.txt")));
   }



   // ---------------------------------------------------------------- a managed repository: its branch

   [Fact]
   public async Task Managed_FollowsOrigin_WhenItsCommitIsContainedInOriginsTip()
   {
      (string source, string target) = await CreateManagedAsync("managed-follow");
      string tip = AppendForgedCommit(source, ("other.txt", "o"));

      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.Equal(tip, HeadSha(target));
      Assert.Equal(tip, MaterializedTip(target));
      Assert.Equal("o", File.ReadAllText(Path.Combine(target, "other.txt")));
   }



   [Fact]
   public async Task Managed_WithALocalCommitAndOriginMovedOn_IsNotMovedOffIt()
   {
      (string source, string target) = await CreateManagedAsync("managed-diverged");
      string local = CommitInWorkingTree(target, "mine.txt", "my work");
      AppendForgedCommit(source, ("other.txt", "o"));

      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.PullAsync(target, Token, CancellationToken.None));

      Assert.Contains("have diverged", ex.Message);
      Assert.Equal(local, HeadSha(target));
      Assert.Equal("my work", File.ReadAllText(Path.Combine(target, "mine.txt")));
   }



   [Fact]
   public async Task Managed_WithALocalCommitAndOriginUnchanged_SyncsWithoutMovingTheBranch()
   {
      (_, string target) = await CreateManagedAsync("managed-ahead");
      string local = CommitInWorkingTree(target, "mine.txt", "my work");

      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.Equal(local, HeadSha(target));
      Assert.Equal("my work", File.ReadAllText(Path.Combine(target, "mine.txt")));
      Assert.Equal("bad", File.ReadAllText(Path.Combine(target, "bad_file.txt")));
   }



   [Fact]
   public async Task Managed_ALocalCommitThatWasSyncedOnce_IsStillNotDropped_WhenOriginMovesOn()
   {
      // A sync with the local commit in place goes through without complaint. That
      // must not turn it into "origin's commit": what counts is whether origin's
      // branch ever pointed at it.
      (string source, string target) = await CreateManagedAsync("managed-synced-local-commit");
      string local = CommitInWorkingTree(target, "mine.txt", "my work");
      await _client.PullAsync(target, Token, CancellationToken.None);
      Assert.Equal(local, HeadSha(target));
      AppendForgedCommit(source, ("other.txt", "from a colleague"));

      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.PullAsync(target, Token, CancellationToken.None));

      Assert.Contains("have diverged", ex.Message);
      Assert.Equal(local, HeadSha(target));
      Assert.Equal("my work", File.ReadAllText(Path.Combine(target, "mine.txt")));
   }



   [Fact]
   public async Task Managed_WhoseOriginWasRewritten_FollowsIt_WhenNobodyCommittedThere()
   {
      (string source, string target) = await CreateManagedAsync("managed-rewritten");
      // Upstream replaces its history: a new root commit, unrelated to the old one.
      string rewritten = RewriteHistory(source, ("bad:file.txt", "bad again"), ("fresh.txt", "fresh"));

      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.Equal(rewritten, HeadSha(target));
      Assert.Equal("fresh", File.ReadAllText(Path.Combine(target, "fresh.txt")));
      Assert.Equal("bad again", File.ReadAllText(Path.Combine(target, "bad_file.txt")));
      Assert.False(File.Exists(Path.Combine(target, "keep.txt")));
   }



   [Fact]
   public async Task Managed_OriginRewrittenAfterAnInterruptedPull_IsStillFollowed()
   {
      (string source, string target) = await CreateManagedAsync("managed-interrupted-then-rewritten");
      string moved = AppendForgedCommit(source, ("other.txt", "o"));
      await InterruptAsync(target, "keep.txt", () => _client.PullAsync(target, Token, CancellationToken.None));
      // The branch moved before the files were written; nobody committed anything.
      Assert.Equal(moved, HeadSha(target));
      Assert.Null(MaterializedTip(target));
      string rewritten = RewriteHistory(source, ("bad:file.txt", "bad again"), ("fresh.txt", "fresh"));

      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.Equal(rewritten, HeadSha(target));
      Assert.Equal(rewritten, MaterializedTip(target));
      Assert.Equal("fresh", File.ReadAllText(Path.Combine(target, "fresh.txt")));
      Assert.False(File.Exists(Path.Combine(target, "other.txt")), "what the interrupted pull wrote and origin no longer has is gone");
      Assert.False(File.Exists(Path.Combine(target, "keep.txt")));
   }



   [Fact]
   public async Task Managed_AnUncoveredInvalidPath_FailsBeforeTheBranchMoves()
   {
      (string source, string target) = await CreateManagedAsync("managed-uncovered");
      string before = HeadSha(target);
      AppendForgedCommit(source, ("worse:new.txt", "w"));

      await PullExpectingInvalidPathsAsync(target);

      // Nothing was delivered, so nothing moved: the branch still names the commit on disk.
      Assert.Equal(before, HeadSha(target));
      Assert.Equal(before, MaterializedTip(target));

      // Resolving the new path then delivers the commit.
      await _client.ApplyRecoveryAsync(target, Recovery(renames: [("worse:new.txt", "worse_new.txt")]), CancellationToken.None);
      Assert.False(string.Equals(before, HeadSha(target), StringComparison.Ordinal), "the branch moved to origin's commit");
      Assert.Equal("w", File.ReadAllText(Path.Combine(target, "worse_new.txt")));
   }



   // ---------------------------------------------------------------- a managed repository: failures

   [Fact]
   public async Task Managed_WhatWouldFailHalfWay_IsFoundBeforeAnythingChanges_AFileWhereAFolderIsNeeded()
   {
      (string source, string target) = await CreateManagedAsync("managed-file-for-folder");
      string before = HeadSha(target);
      File.WriteAllText(Path.Combine(target, "docs"), "a file of the user's");
      AppendForgedCommit(source, ("docs/a.txt", "a"));

      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.PullAsync(target, Token, CancellationToken.None));

      Assert.Contains("'docs' is a file in this folder", ex.Message);
      Assert.Equal(before, HeadSha(target));
      Assert.Equal(before, MaterializedTip(target));
      Assert.Equal("a file of the user's", File.ReadAllText(Path.Combine(target, "docs")));
   }



   [Fact]
   public async Task Managed_WhatWouldFailHalfWay_IsFoundBeforeAnythingChanges_ALinkOnTheWay()
   {
      (string source, string target) = await CreateManagedAsync("managed-link");
      string before = HeadSha(target);
      string elsewhere = Path.Combine(_root, "elsewhere");
      Directory.CreateDirectory(elsewhere);
      string link = Path.Combine(target, "vendor");
      WorkingTreeTests.CreateDirectoryLink(link, elsewhere);
      try
      {
         AppendForgedCommit(source, ("vendor/lib.txt", "lib"));

         InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
             () => _client.PullAsync(target, Token, CancellationToken.None));

         Assert.Contains("is a link to another location", ex.Message);
         Assert.Equal(before, HeadSha(target));
         Assert.Equal(before, MaterializedTip(target));
         Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
      }
      finally
      {
         WorkingTreeTests.RemoveLink(link);
      }
   }



   [Fact]
   public async Task Managed_APullInterruptedHalfWay_IsCompletedByTheNext_AndWhatLeftTheTreeIsStillRemoved()
   {
      (string source, string target) = await CreateManagedAsync("managed-interrupted-pull");
      ReplaceEntry(source, remove: "keep.txt", add: ("zz-new.txt", "new"));
      string tip = AppendForgedCommit(source, ("aa-new.txt", "new"));
      // "bad_file.txt" is held open: the pull fails after the branch moved and
      // "keep.txt" was removed.
      await InterruptAsync(target, "bad_file.txt", () => _client.PullAsync(target, Token, CancellationToken.None));
      Assert.Null(MaterializedTip(target));
      Assert.False(IsFlagSet(target, PendingKey), "the never-checked-out marker is not for a repository that has a tree");

      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.Equal(tip, HeadSha(target));
      Assert.Equal(tip, MaterializedTip(target));
      Assert.Equal("new", File.ReadAllText(Path.Combine(target, "aa-new.txt")));
      Assert.Equal("new", File.ReadAllText(Path.Combine(target, "zz-new.txt")));
      Assert.False(File.Exists(Path.Combine(target, "keep.txt")));
   }



   [Fact]
   public async Task Managed_ReResolvingAtTheSameCommit_ThatFailsHalfWay_IsCompletedByTheNextPull()
   {
      (_, string target) = await CreateManagedAsync("managed-interrupted-resolve");
      string tip = HeadSha(target);
      // A different name for the same path; the apply fails after the old name was
      // removed, before everything is written.
      await InterruptAsync(
          target, "keep.txt",
          () => _client.ApplyRecoveryAsync(target, Recovery(renames: [("bad:file.txt", "renamed.txt")]), CancellationToken.None));
      Assert.Null(MaterializedTip(target));

      // The tip did not move, but the tree is unfinished: "already at the tip" does not apply.
      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.Equal(tip, MaterializedTip(target));
      Assert.Equal("bad", File.ReadAllText(Path.Combine(target, "renamed.txt")));
      Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
      Assert.False(File.Exists(Path.Combine(target, "bad_file.txt")));
   }



   // ---------------------------------------------------------------- a managed repository: its recovery file

   [Fact]
   public async Task Managed_WhoseRecoveryFileWasDeleted_AsTheMessageForACorruptOneAdvises_CanBeResolvedAgain()
   {
      (string source, string target) = await CreateManagedAsync("managed-file-deleted");
      File.Delete(RecoveryFilePath(target));

      // It is still known to be recovery-managed, so the pull reports the invalid
      // path again instead of treating the renamed tree as an ordinary clone...
      await PullExpectingInvalidPathsAsync(target);
      // ...and resolving rebuilds the file instead of refusing a "checked-out clone".
      await _client.ApplyRecoveryAsync(target, Recovery(renames: [("bad:file.txt", "bad_file.txt")]), CancellationToken.None);

      Assert.Equal(HeadSha(target), MaterializedTip(target));
      string tip = AppendForgedCommit(source, ("other.txt", "o"));
      await _client.PullAsync(target, Token, CancellationToken.None);
      Assert.Equal(tip, HeadSha(target));
   }



   [Fact]
   public async Task Managed_ByTheReleasedBuild_WhichWroteNoIndexAndNoFlag_KeepsUpdatingAfterTheUpgrade()
   {
      (string source, string target) = await CreateManagedAsync("managed-released-build");
      // What 1.1.0 leaves: the recovery file with its manifest and tip, but no index
      // and no managed flag.
      File.Delete(Path.Combine(target, ".git", "index"));
      UnsetFlag(target, ManagedKey);
      string tip = AppendForgedCommit(source, ("other.txt", "o"));

      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.Equal(tip, HeadSha(target));
      Assert.Equal("o", File.ReadAllText(Path.Combine(target, "other.txt")));
      Assert.True(IsFlagSet(target, ManagedKey), "once its file has been read, the repository is known to be managed");
   }



   [Fact]
   public async Task Managed_ByTheReleasedBuild_IsFlaggedOnAPullThatHasNothingToDo_SoLosingTheFileLaterIsNotADeadEnd()
   {
      (_, string target) = await CreateManagedAsync("managed-released-build-idle");
      UnsetFlag(target, ManagedKey);

      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.True(IsFlagSet(target, ManagedKey));
   }



   [Fact]
   public async Task Managed_ARecoveryFileFromABuildThatRecordedLess_IsCompleted()
   {
      (string source, string target) = await CreateManagedAsync("managed-old-file");
      // Files written before the manifest existed have neither list nor tip.
      JsonNode document = JsonNode.Parse(File.ReadAllText(RecoveryFilePath(target)))!;
      document.AsObject().Remove("MaterializedPaths");
      document.AsObject().Remove("MaterializedTip");
      File.WriteAllText(RecoveryFilePath(target), document.ToJsonString());
      string tip = AppendForgedCommit(source, ("other.txt", "o"));

      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.Equal(tip, HeadSha(target));
      Assert.Equal(tip, MaterializedTip(target));
      Assert.Equal("o", File.ReadAllText(Path.Combine(target, "other.txt")));
   }



   [Fact]
   public async Task Managed_WithATwinThatDiffersOnlyByCaseSkipped_KeepsUpdating()
   {
      // Skipping one of two names that differ only by case leaves a tree git itself
      // reads as changed; that must not stop later updates.
      string source = CreateForgedRepo(_root, ("readme.md", "lower"), ("README.md", "upper"), ("keep.txt", "keep"));
      string target = await CloneExpectingInvalidPathsAsync(source, "managed-twins");
      using(var repo = new Repository(source))
      {
         Assert.NotNull(repo.Head.Tip["README.md"]);
      }
      await _client.ApplyRecoveryAsync(target, Recovery(skips: ["README.md"]), CancellationToken.None);
      Assert.Equal("lower", File.ReadAllText(Path.Combine(target, "readme.md")));
      string tip = AppendForgedCommit(source, ("other.txt", "o"));

      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.Equal(tip, HeadSha(target));
      Assert.Equal("o", File.ReadAllText(Path.Combine(target, "other.txt")));
      Assert.Equal("lower", File.ReadAllText(Path.Combine(target, "readme.md")));
   }



   // ---------------------------------------------------------------- what git sees afterwards

   [Fact]
   public async Task AfterAnApply_TheIndexDescribesTheCommit_SoOnlyRenamedAndSkippedPathsDiffer()
   {
      string source = CreateForgedRepo(
          _root, ("bad:file.txt", "bad"), ("skip:me.txt", "skipped"), ("keep.txt", "keep"), ("src/app.cs", "code"));
      string target = await CloneExpectingInvalidPathsAsync(source, "index");

      await _client.ApplyRecoveryAsync(
          target, Recovery(renames: [("bad:file.txt", "bad_file.txt")], skips: ["skip:me.txt"]), CancellationToken.None);

      using var repo = new Repository(target);
      Assert.Equal(
          new[] { "bad:file.txt", "keep.txt", "skip:me.txt", "src/app.cs" },
          repo.Index.Select(e => e.Path).Order(StringComparer.Ordinal),
          StringComparer.Ordinal);
      RepositoryStatus status = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true });
      // The files that kept their names are clean; nothing reads as "everything deleted".
      Assert.Equal(FileStatus.Unaltered, repo.RetrieveStatus("keep.txt"));
      Assert.Equal(FileStatus.Unaltered, repo.RetrieveStatus("src/app.cs"));
      Assert.Equal(
          new[] { "bad:file.txt", "skip:me.txt" },
          status.Missing.Select(e => e.FilePath).Order(StringComparer.Ordinal),
          StringComparer.Ordinal);
      Assert.Equal("bad_file.txt", Assert.Single(status.Untracked).FilePath);
   }



   [Fact]
   public async Task AttributeFiles_ThatChangeUpstream_ApplyToFilesWrittenBeforeThem_NotTheStaleOneOnDisk()
   {
      string source = CreateForgedRepo(
          _root,
          ("!early.txt", "one\ntwo\n"),
          (".gitattributes", "*.txt text eol=crlf\n"),
          ("bad:file.txt", "one\ntwo\n"));
      string target = await CloneExpectingInvalidPathsAsync(source, "attributes-change");
      UseCrLfByDefault(target);
      await _client.ApplyRecoveryAsync(target, Recovery(renames: [("bad:file.txt", "bad_file.txt")]), CancellationToken.None);
      Assert.Equal("one\r\ntwo\r\n", File.ReadAllText(Path.Combine(target, "!early.txt")));
      // Upstream switches the line endings; the old attributes file is still on disk
      // when "!early.txt", which sorts before it, is rewritten.
      ReplaceEntry(source, remove: ".gitattributes", add: (".gitattributes", "*.txt text eol=lf\n"));
      AppendForgedCommit(source, ("!early.txt", "one\ntwo\nthree\n"));

      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(Path.Combine(target, "!early.txt")));
      Assert.Equal("one\ntwo\n", File.ReadAllText(Path.Combine(target, "bad_file.txt")));
   }



   // ---------------------------------------------------------------- the newer choice wins

   [Fact]
   public async Task AStoredSkip_IsReplacedByALaterRename_OfTheSamePath()
   {
      string source = CreateForgedRepo(_root, ("bad:file.txt", "bad"), ("keep.txt", "keep"));
      string target = await CloneExpectingInvalidPathsAsync(source, "skip-then-rename");
      await _client.ApplyRecoveryAsync(target, Recovery(skips: ["bad:file.txt"]), CancellationToken.None);
      Assert.False(File.Exists(Path.Combine(target, "bad_file.txt")));

      await _client.ApplyRecoveryAsync(target, Recovery(renames: [("bad:file.txt", "bad_file.txt")]), CancellationToken.None);

      Assert.Equal("bad", File.ReadAllText(Path.Combine(target, "bad_file.txt")));
      Assert.Contains("bad_file.txt", File.ReadAllText(RecoveryFilePath(target)));
   }



   [Fact]
   public async Task AStoredRename_IsReplacedByALaterSkip_OfTheSamePath()
   {
      (_, string target) = await CreateManagedAsync("rename-then-skip");
      Assert.True(File.Exists(Path.Combine(target, "bad_file.txt")));

      await _client.ApplyRecoveryAsync(target, Recovery(skips: ["bad:file.txt"]), CancellationToken.None);

      Assert.False(File.Exists(Path.Combine(target, "bad_file.txt")), "the file written under the old choice is removed");
      Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
      Assert.DoesNotContain("bad_file.txt", File.ReadAllText(RecoveryFilePath(target)));
   }



   // ---------------------------------------------------------------- an empty repository is only empty while nothing is in it

   [Fact]
   public async Task EmptyClone_WithAFileOfTheUsersWhereTheRepositoryHasOne_IsRefused_AndTheFileIsKept()
   {
      string source = Path.Combine(_root, "empty-source-2");
      Repository.Init(source);
      string target = Path.Combine(_root, "empty-clone-2");
      await _client.CloneAsync(source, target, Token, null, CancellationToken.None);
      File.WriteAllText(Path.Combine(target, "keep.txt"), "mine, put here by hand");
      FirstForgedCommit(source, ("bad:file.txt", "bad"), ("keep.txt", "origin's"));
      await PullExpectingInvalidPathsAsync(target);

      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.ApplyRecoveryAsync(target, Recovery(renames: [("bad:file.txt", "bad_file.txt")]), CancellationToken.None));

      Assert.Contains("'keep.txt' is already in this folder", ex.Message);
      Assert.Equal("mine, put here by hand", File.ReadAllText(Path.Combine(target, "keep.txt")));
      Assert.False(File.Exists(Path.Combine(target, "bad_file.txt")));
      Assert.False(IsFlagSet(target, ManagedKey));
      Assert.False(File.Exists(RecoveryFilePath(target)));

      // With the file out of the way the empty clone resolves as before.
      File.Delete(Path.Combine(target, "keep.txt"));
      await _client.ApplyRecoveryAsync(target, Recovery(renames: [("bad:file.txt", "bad_file.txt")]), CancellationToken.None);
      Assert.Equal("origin's", File.ReadAllText(Path.Combine(target, "keep.txt")));
   }



   [Fact]
   public async Task ABranchWithoutCommitsInACheckedOutClone_IsStillACheckedOutClone_AndIsRefused()
   {
      // What "git checkout --orphan topic" leaves: no commit on the branch, the index
      // and the working tree of the branch it was started from.
      (string source, string target) = await CloneOrdinaryAsync(("readme.txt", "v1"), ("work.txt", "work"));
      using(var repo = new Repository(target))
      {
         repo.Refs.UpdateTarget("HEAD", "refs/heads/topic");
      }
      File.WriteAllText(Path.Combine(target, "work.txt"), "my local edit");
      using(var repo = new Repository(source))
      {
         Commit onTopic = NewRootCommit(repo, [("bad:file.txt", "bad"), ("work.txt", "origin's")]);
         repo.Refs.Add("refs/heads/topic", onTopic.Id);
      }
      Dictionary<string, string> filesBefore = ReadAllFiles(target);

      // The pull does not take "no commit yet" for "nothing checked out"...
      InvalidOperationException pull = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.PullAsync(target, Token, CancellationToken.None));
      Assert.Contains("has no commits here yet, but files are staged", pull.Message);

      // ...and neither does resolving.
      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.ApplyRecoveryAsync(target, Recovery(renames: [("bad:file.txt", "bad_file.txt")]), CancellationToken.None));

      Assert.Contains("is checked out normally", ex.Message);
      Assert.Equal(filesBefore, ReadAllFiles(target));
      Assert.False(IsFlagSet(target, ManagedKey));
   }



   [Fact]
   public async Task ABranchWithoutCommitsInACheckedOutClone_IsNotCheckedOutOver_ByAPull_EvenWithValidPaths()
   {
      (string source, string target) = await CloneOrdinaryAsync(("readme.txt", "v1"), ("work.txt", "work"));
      using(var repo = new Repository(target))
      {
         repo.Refs.UpdateTarget("HEAD", "refs/heads/topic");
      }
      File.WriteAllText(Path.Combine(target, "work.txt"), "my local edit");
      using(var repo = new Repository(source))
      {
         Commit onTopic = NewRootCommit(repo, [("work.txt", "origin's")]);
         repo.Refs.Add("refs/heads/topic", onTopic.Id);
      }

      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.PullAsync(target, Token, CancellationToken.None));

      Assert.Contains("gclo does not check out over them", ex.Message);
      Assert.Equal("my local edit", File.ReadAllText(Path.Combine(target, "work.txt")));
   }



   // ---------------------------------------------------------------- what counts as origin's commit

   [Fact]
   public async Task Managed_ACommitThatWasPushed_IsNotTakenForOneOfOrigins_WhenOriginIsRewrittenWithoutIt()
   {
      (string source, string target) = await CreateManagedAsync("managed-pushed");
      string mine = CommitInWorkingTree(target, "mine.txt", "my work");
      // What "git push" records in this clone: the remote-tracking branch now points
      // at the pushed commit, and its reflog says a push put it there.
      using(var repo = new Repository(target))
      {
         repo.Refs.UpdateTarget(repo.Refs[repo.Head.TrackedBranch.CanonicalName], new ObjectId(mine), "update by push");
      }
      // A colleague force-pushes a history that does not have the commit.
      RewriteHistory(source, ("bad:file.txt", "bad again"), ("keep.txt", "keep"), ("fresh.txt", "fresh"));

      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.PullAsync(target, Token, CancellationToken.None));

      Assert.Contains("have diverged", ex.Message);
      Assert.Equal(mine, HeadSha(target));
      Assert.Equal("my work", File.ReadAllText(Path.Combine(target, "mine.txt")));
   }



   [Fact]
   public async Task Managed_ABranchThatTracksAnotherLocalBranch_IsNeverMovedOffItsCommits()
   {
      (_, string target) = await CreateManagedAsync("managed-local-upstream");
      string start = HeadSha(target);
      string mine = CommitInWorkingTree(target, "mine.txt", "my work");
      using(var repo = new Repository(target))
      {
         // "I committed on the wrong branch": the commit goes to a branch that tracks
         // the local one, which is put back and later moves on without it.
         string main = repo.Head.FriendlyName;
         Branch feature = repo.CreateBranch("feature", repo.Head.Tip);
         repo.Branches.Update(feature, b =>
         {
            b.Remote = ".";
            b.UpstreamBranch = "refs/heads/" + main;
         });
         Signature signature = MakeSignature();
         Commit startCommit = repo.Lookup<Commit>(start);
         Commit elsewhere = repo.ObjectDatabase.CreateCommit(
             signature, signature, "main moves on", startCommit.Tree, [startCommit], prettifyMessage: false);
         repo.Refs.UpdateTarget(repo.Refs["refs/heads/" + main], elsewhere.Id);
         repo.Refs.UpdateTarget("HEAD", "refs/heads/feature");
      }

      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.PullAsync(target, Token, CancellationToken.None));

      Assert.Contains("have diverged", ex.Message);
      Assert.Equal(mine, HeadSha(target));
   }



   [Fact]
   public async Task Managed_OriginRewritten_IsFollowed_GoingByAnEarlierFetch_NotOnlyByTheLatest()
   {
      (string source, string target) = await CreateManagedAsync("managed-rewritten-after-two");
      AppendForgedCommit(source, ("second.txt", "2"));
      await _client.PullAsync(target, Token, CancellationToken.None); // the branch is where a fetch put it
      string rewritten = RewriteHistory(source, ("bad:file.txt", "bad again"), ("keep.txt", "keep"));

      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.Equal(rewritten, HeadSha(target));
      Assert.False(File.Exists(Path.Combine(target, "second.txt")));
   }



   [Fact]
   public async Task Managed_OriginRewritten_IsFollowed_WhenTheOlderRecordsOfWhatWasFetchedHaveExpired()
   {
      (string source, string target) = await CreateManagedAsync("managed-expired-reflog");
      DeleteReflogOfTheTrackedBranch(target);
      string rewritten = RewriteHistory(source, ("bad:file.txt", "bad again"), ("keep.txt", "keep"));

      // The fetch itself records where the branch was before it moved it.
      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.Equal(rewritten, HeadSha(target));
   }



   [Fact]
   public async Task Managed_WithNoRecordOfWhatWasFetched_StillFastForwards_AndRefusesARewriteWithoutClaimingLocalCommits()
   {
      (string source, string target) = await CreateManagedAsync("managed-no-reflog");
      using(var repo = new Repository(target))
      {
         repo.Config.Set("core.logAllRefUpdates", false, ConfigurationLevel.Local);
      }
      DeleteReflogOfTheTrackedBranch(target);

      string next = AppendForgedCommit(source, ("more.txt", "m"));
      await _client.PullAsync(target, Token, CancellationToken.None);
      Assert.Equal(next, HeadSha(target)); // contained in origin's tip: no record is needed

      RewriteHistory(source, ("bad:file.txt", "bad again"), ("keep.txt", "keep"));
      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.PullAsync(target, Token, CancellationToken.None));

      Assert.Contains("have diverged", ex.Message);
      Assert.Contains("the record of what was fetched is gone", ex.Message);
      Assert.DoesNotContain("has local commits", ex.Message);
      Assert.Equal(next, HeadSha(target));
   }



   [Fact]
   public async Task NeverCheckedOut_WithACommitMadeByHand_IsNotMovedOffIt_ByAPull()
   {
      string source = CreateForgedRepo(_root, ("bad:file.txt", "bad"), ("keep.txt", "keep"));
      string target = await CloneExpectingInvalidPathsAsync(source, "pending-with-commit");
      string mine;
      using(var repo = new Repository(target))
      {
         Signature signature = MakeSignature();
         Commit commit = repo.ObjectDatabase.CreateCommit(
             signature, signature, "made by hand", repo.Head.Tip.Tree, [repo.Head.Tip], prettifyMessage: false);
         repo.Refs.UpdateTarget(repo.Refs.Head.ResolveToDirectReference(), commit.Id);
         mine = commit.Sha;
      }

      await PullExpectingInvalidPathsAsync(target);

      Assert.Equal(mine, HeadSha(target));
   }



   // ---------------------------------------------------------------- a pull that has nothing to write, writes nothing

   [Fact]
   public async Task Managed_ACommitMadeOnTop_WithOriginUnchanged_LeavesEveryOtherFileAsItIs()
   {
      (_, string target) = await CreateManagedAsync("managed-commit-and-edit");
      File.WriteAllText(Path.Combine(target, "keep.txt"), "edited, not committed");
      string mine = CommitInWorkingTree(target, "mine.txt", "my work");

      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.Equal(mine, HeadSha(target));
      Assert.Equal("edited, not committed", File.ReadAllText(Path.Combine(target, "keep.txt")));
      Assert.Equal("bad", File.ReadAllText(Path.Combine(target, "bad_file.txt")));
      Assert.Equal("my work", File.ReadAllText(Path.Combine(target, "mine.txt")));
   }



   [Fact]
   public async Task Managed_WhoseBranchWasMovedBackByHand_IsBroughtToOriginsTipAgain()
   {
      (string source, string target) = await CreateManagedAsync("managed-moved-back");
      string first = HeadSha(target);
      string tip = AppendForgedCommit(source, ("second.txt", "2"));
      await _client.PullAsync(target, Token, CancellationToken.None);
      using(var repo = new Repository(target))
      {
         repo.Refs.UpdateTarget(repo.Refs.Head.ResolveToDirectReference(), new ObjectId(first)); // "git reset --soft HEAD~1"
      }

      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.Equal(tip, HeadSha(target));
      Assert.Equal(tip, MaterializedTip(target));
   }



   [Fact]
   public async Task Managed_ARecordedTipThatIsNotACommit_IsNotTrusted_AndTheTreeIsWrittenAgain()
   {
      (_, string target) = await CreateManagedAsync("managed-odd-tip");
      File.WriteAllText(Path.Combine(target, "keep.txt"), "changed on disk");
      string file = RecoveryFilePath(target);
      JsonNode document = JsonNode.Parse(File.ReadAllText(file))!;
      document["MaterializedTip"] = "not a commit id";
      File.WriteAllText(file, document.ToJsonString());

      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
      Assert.Equal(HeadSha(target), MaterializedTip(target));
   }



   // ---------------------------------------------------------------- found before anything changes

   [Fact]
   public async Task Managed_AnotherGitProcessAtWork_IsFoundBeforeAnythingChanges()
   {
      (string source, string target) = await CreateManagedAsync("managed-index-lock");
      string before = HeadSha(target);
      string tip = AppendForgedCommit(source, ("more.txt", "m"));
      string lockFile = Path.Combine(target, ".git", "index.lock");
      File.WriteAllText(lockFile, "");

      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.PullAsync(target, Token, CancellationToken.None));

      Assert.Contains("its index is locked", ex.Message);
      Assert.Equal(before, HeadSha(target));
      Assert.Equal(before, MaterializedTip(target));

      File.Delete(lockFile);
      await _client.PullAsync(target, Token, CancellationToken.None);
      Assert.Equal(tip, HeadSha(target));
   }



   [Fact]
   public async Task Managed_AFolderWithSomeonesFilesWhereTheRepositoryNowHasAFile_IsFoundBeforeAnythingChanges()
   {
      (string source, string target) = await CreateManagedAsync("managed-folder-in-the-way");
      string before = HeadSha(target);
      Directory.CreateDirectory(Path.Combine(target, "notes", "deeper"));
      File.WriteAllText(Path.Combine(target, "notes", "deeper", "mine.txt"), "mine");
      AppendForgedCommit(source, ("notes", "origin's file"));

      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.PullAsync(target, Token, CancellationToken.None));

      Assert.Contains("'notes' is a folder with files in it ('notes/deeper/mine.txt' is one)", ex.Message);
      Assert.Equal(before, HeadSha(target));
      Assert.Equal(before, MaterializedTip(target));
      Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "notes", "deeper", "mine.txt")));
   }



   [Fact]
   public async Task Managed_AFolderOfItsOwnThatUpstreamTurnedIntoAFile_IsReplaced()
   {
      (string source, string target) = await CreateManagedAsync("managed-own-folder-to-file");
      AppendForgedCommit(source, ("notes/a.txt", "a"), ("notes/deeper/b.txt", "b"));
      await _client.PullAsync(target, Token, CancellationToken.None);
      using(var repo = new Repository(source))
      {
         Commit head = repo.Head.Tip;
         TreeDefinition definition = TreeDefinition.From(head.Tree).Remove("notes");
         definition.Add("notes", NewBlob(repo, "now a file"), Mode.NonExecutableFile);
         Signature signature = MakeSignature();
         Commit commit = repo.ObjectDatabase.CreateCommit(
             signature, signature, "folder becomes file", repo.ObjectDatabase.CreateTree(definition), [head], prettifyMessage: false);
         repo.Refs.UpdateTarget(repo.Refs.Head.ResolveToDirectReference(), commit.Id);
      }

      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.Equal("now a file", File.ReadAllText(Path.Combine(target, "notes")));
   }



   [Fact]
   public async Task Managed_ALinkOnTheWayToAFileThatLeftTheTree_IsFoundBeforeAnythingChanges()
   {
      (string source, string target) = await CreateManagedAsync("managed-link-to-stale");
      AppendForgedCommit(source, ("sub/old.txt", "old"));
      await _client.PullAsync(target, Token, CancellationToken.None);
      string before = HeadSha(target);
      // The folder gclo wrote into is replaced by a link to a folder elsewhere that
      // happens to have a file of the same name.
      string elsewhere = Path.Combine(_root, "elsewhere-stale");
      Directory.CreateDirectory(elsewhere);
      File.WriteAllText(Path.Combine(elsewhere, "old.txt"), "someone else's");
      Directory.Delete(Path.Combine(target, "sub"), recursive: true);
      string link = Path.Combine(target, "sub");
      WorkingTreeTests.CreateDirectoryLink(link, elsewhere);
      try
      {
         ReplaceEntry(source, remove: "sub/old.txt", add: ("other.txt", "o"));

         InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
             () => _client.PullAsync(target, Token, CancellationToken.None));

         Assert.Contains("is a link to another location", ex.Message);
         Assert.Equal(before, HeadSha(target));
         Assert.Equal(before, MaterializedTip(target));
         Assert.Equal("someone else's", File.ReadAllText(Path.Combine(elsewhere, "old.txt")));
      }
      finally
      {
         WorkingTreeTests.RemoveLink(link);
      }
   }



   [Theory]
   [InlineData("docs", "docs/a.txt")]
   [InlineData("Docs", "docs/a.txt")]
   public async Task Managed_AFileOfItsOwnThatUpstreamTurnedIntoAFolder_IsNotTakenForSomeoneElses(string file, string inFolder)
   {
      if(!string.Equals(file, "docs", StringComparison.Ordinal) && !OperatingSystem.IsWindows())
      {
         return; // only a file system that ignores case sees 'Docs' in the way of 'docs'
      }
      (string source, string target) = await CreateManagedAsync("managed-own-file-to-folder-" + file);
      AppendForgedCommit(source, (file, "a file"));
      await _client.PullAsync(target, Token, CancellationToken.None);
      ReplaceEntry(source, remove: file, add: (inFolder, "in a folder"));

      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.Equal("in a folder", File.ReadAllText(Path.Combine(target, "docs", "a.txt")));
   }



   [Fact]
   public async Task Managed_AFileThatLeftTheTree_WhereAFolderOfTheUsersNowSits_LeavesTheFolder_AndTheSyncSucceeds()
   {
      (string source, string target) = await CreateManagedAsync("managed-stale-is-a-folder");
      AppendForgedCommit(source, ("notes", "a file"));
      await _client.PullAsync(target, Token, CancellationToken.None);
      File.Delete(Path.Combine(target, "notes"));
      Directory.CreateDirectory(Path.Combine(target, "notes"));
      File.WriteAllText(Path.Combine(target, "notes", "mine.txt"), "mine");
      ReplaceEntry(source, remove: "notes", add: ("other.txt", "o"));
      string tip = HeadSha(source);

      await _client.PullAsync(target, Token, CancellationToken.None);
      await _client.PullAsync(target, Token, CancellationToken.None); // and it stays fine

      Assert.Equal(tip, HeadSha(target));
      Assert.Equal(tip, MaterializedTip(target));
      Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "notes", "mine.txt")));
      Assert.Equal("o", File.ReadAllText(Path.Combine(target, "other.txt")));
   }



   // ---------------------------------------------------------------- what an interrupted apply had and was about to have

   [Fact]
   public async Task Managed_AFileThatCouldNotBeRemovedWhenThePullWasInterrupted_IsRemovedByTheNext()
   {
      (string source, string target) = await CreateManagedAsync("managed-stale-held");
      string tip = HeadSha(source);
      ReplaceEntry(source, remove: "keep.txt", add: ("zz-new.txt", "new"));
      tip = HeadSha(source);
      // "keep.txt" left the tree and is held open: removing it is what fails.
      await InterruptAsync(target, "keep.txt", () => _client.PullAsync(target, Token, CancellationToken.None));
      Assert.Null(MaterializedTip(target));
      Assert.True(File.Exists(Path.Combine(target, "keep.txt")));

      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.Equal(tip, MaterializedTip(target));
      Assert.False(File.Exists(Path.Combine(target, "keep.txt")), "it is still known to be gclo's, and it left the tree");
   }



   [Fact]
   public async Task Managed_AFileWrittenBeforeThePullWasInterrupted_IsRemovedByTheNext_WhenItHasLeftTheTreeByThen()
   {
      (string source, string target) = await CreateManagedAsync("managed-new-then-gone");
      AppendForgedCommit(source, ("aa-new.txt", "new"), ("keep.txt", "changed"));
      // "aa-new.txt" is written, then "keep.txt" (held open) stops the pull.
      await InterruptAsync(target, "keep.txt", () => _client.PullAsync(target, Token, CancellationToken.None));
      Assert.True(File.Exists(Path.Combine(target, "aa-new.txt")));
      ReplaceEntry(source, remove: "aa-new.txt", add: ("other.txt", "o"));

      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.False(File.Exists(Path.Combine(target, "aa-new.txt")), "written by the interrupted pull, so gclo's to remove");
      Assert.Equal("changed", File.ReadAllText(Path.Combine(target, "keep.txt")));
      Assert.Equal("o", File.ReadAllText(Path.Combine(target, "other.txt")));
   }



   // ---------------------------------------------------------------- written by the released build, recovery file lost

   [Fact]
   public async Task ResolvedByTheReleasedBuild_WhoseRecoveryFileWasLost_CanBeResolvedAgain_AsTheMessageAdvises()
   {
      (string source, string target) = await CreateManagedAsync("released-build-file-lost");
      // What 1.1.0 leaves (no index, no managed flag), with its recovery file gone
      // before this build ever read it.
      File.Delete(Path.Combine(target, ".git", "index"));
      UnsetFlag(target, ManagedKey);
      File.Delete(RecoveryFilePath(target));

      // The sync reports the paths again, which is what offers Resolve in the app
      // and what --sanitize-paths acts on...
      await PullExpectingInvalidPathsAsync(target);
      // ...and resolving rebuilds the file.
      await _client.ApplyRecoveryAsync(target, Recovery(renames: [("bad:file.txt", "bad_file.txt")]), CancellationToken.None);

      Assert.Equal(HeadSha(target), MaterializedTip(target));
      Assert.True(IsFlagSet(target, ManagedKey));
      string tip = AppendForgedCommit(source, ("other.txt", "o"));
      await _client.PullAsync(target, Token, CancellationToken.None);
      Assert.Equal(tip, HeadSha(target));
   }



   [Fact]
   public async Task ARefusedApply_LeavesTheRepositorysConfigurationAsItWas()
   {
      (string source, string target) = await CloneOrdinaryAsync(("readme.txt", "v1"));
      AppendForgedCommit(source, ("bad:file.txt", "bad"));
      await PullExpectingInvalidPathsAsync(target);
      using(var repo = new Repository(target))
      {
         repo.Config.Unset("core.longpaths", ConfigurationLevel.Local); // the pull set it; the refused apply must not
      }
      string config = Path.Combine(target, ".git", "config");
      string before = File.ReadAllText(config);

      await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.ApplyRecoveryAsync(target, Recovery(renames: [("bad:file.txt", "bad_file.txt")]), CancellationToken.None));

      Assert.Equal(before, File.ReadAllText(config));
   }



   // ---------------------------------------------------------------- what the released build can leave behind

   /// <summary>
   /// Puts a managed repository into the state a pull of the released build leaves
   /// when it fails after its first step: the branch is at origin's new commit, the
   /// tree and the recovery file are still at the previous one, and there is neither
   /// an index nor the managed flag.
   /// </summary>
   private static void LeaveAsAFailedPullOfTheReleasedBuild(string target)
   {
      using var repo = new Repository(target);
      Commands.Fetch(repo, "origin", [], null, null);
      repo.Refs.UpdateTarget(repo.Refs.Head.ResolveToDirectReference(), repo.Head.TrackedBranch.Tip.Id);
      repo.Config.Unset(ManagedKey, ConfigurationLevel.Local);
      File.Delete(Path.Combine(repo.Info.Path, "index"));
   }



   [Fact]
   public async Task AfterAPullOfTheReleasedBuildThatWasInterrupted_TheNextPullWritesTheTree()
   {
      (string source, string target) = await CreateManagedAsync("released-build-interrupted");
      string first = HeadSha(target);
      string tip = AppendForgedCommit(source, ("new.txt", "new"), ("keep.txt", "changed"));
      LeaveAsAFailedPullOfTheReleasedBuild(target);
      Assert.Equal(tip, HeadSha(target));
      Assert.Equal(first, MaterializedTip(target));

      // The branch being ahead of what was written is origin's doing here, not a
      // commit made in the folder: the tree still has to be written.
      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.Equal(tip, MaterializedTip(target));
      Assert.Equal("new", File.ReadAllText(Path.Combine(target, "new.txt")));
      Assert.Equal("changed", File.ReadAllText(Path.Combine(target, "keep.txt")));
   }



   [Fact]
   public async Task AfterAPullOfTheReleasedBuildThatFailedOnANewInvalidPath_TheNextPullReportsItAgain()
   {
      (string source, string target) = await CreateManagedAsync("released-build-new-invalid");
      AppendForgedCommit(source, ("another:bad.txt", "bad too"));
      LeaveAsAFailedPullOfTheReleasedBuild(target);

      // Reporting nothing would take the Resolve link away and leave the tree a commit behind.
      InvalidRepositoryPathsException ex = await Assert.ThrowsAsync<InvalidRepositoryPathsException>(
          () => _client.PullAsync(target, Token, CancellationToken.None));

      Assert.Contains(ex.Paths, p => string.Equals(p.RepoPath, "another:bad.txt", StringComparison.Ordinal));
   }



   // ---------------------------------------------------------------- a tree that only looks like one gclo wrote

   [Fact]
   public async Task AClonedRepositoryWhoseIndexWasDeleted_KeepsItsEditedFile_WhenResolved()
   {
      (string source, string target) = await CloneOrdinaryAsync(("readme.txt", "v1"), ("work.txt", "work"));
      File.WriteAllText(Path.Combine(target, "work.txt"), "my local edit");
      File.WriteAllText(Path.Combine(target, "untracked.txt"), "mine");
      File.Delete(Path.Combine(target, ".git", "index"));
      AppendForgedCommit(source, ("bad:file.txt", "bad"), ("readme.txt", "v2"));
      await PullExpectingInvalidPathsAsync(target);

      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.ApplyRecoveryAsync(target, Recovery(renames: [("bad:file.txt", "bad_file.txt")]), CancellationToken.None));

      Assert.Contains("'work.txt' is already in this folder", ex.Message);
      Assert.Equal("my local edit", File.ReadAllText(Path.Combine(target, "work.txt")));
      Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "untracked.txt")));
      Assert.Equal("v1", File.ReadAllText(Path.Combine(target, "readme.txt")));
      Assert.False(IsFlagSet(target, ManagedKey));
   }



   [Fact]
   public async Task AClonedRepositoryWhoseIndexWasDeleted_AndThatHasNothingToRename_StaysAnOrdinaryClone()
   {
      (_, string target) = await CloneOrdinaryAsync(("readme.txt", "v1"), ("work.txt", "work"));
      File.Delete(Path.Combine(target, ".git", "index"));

      await _client.PullAsync(target, Token, CancellationToken.None); // nothing new on origin

      Assert.False(IsFlagSet(target, ManagedKey), "a tree with nothing to rename is not one path recovery wrote");
      Assert.False(File.Exists(RecoveryFilePath(target)));

      // And it is pulled like any clone: with an edit in it, and nothing new on
      // origin, the sync goes through and the edit is still there.
      File.WriteAllText(Path.Combine(target, "work.txt"), "my local edit");
      await _client.PullAsync(target, Token, CancellationToken.None);

      Assert.Equal("my local edit", File.ReadAllText(Path.Combine(target, "work.txt")));
      Assert.False(IsFlagSet(target, ManagedKey));
      Assert.False(File.Exists(RecoveryFilePath(target)));
   }



   [Fact]
   public async Task ARepositoryClonedByGitWithoutACheckout_IsNotTakenForOneGcloWrote()
   {
      string source = CreateForgedRepo(_root, ("readme.txt", "v1"));
      string target = Path.Combine(_root, "cloned-by-git");
      Repository.Clone(source, target, new CloneOptions { Checkout = false });
      File.WriteAllText(Path.Combine(target, "readme.txt"), "put here by hand");
      AppendForgedCommit(source, ("bad:file.txt", "bad"));
      await PullExpectingInvalidPathsAsync(target);

      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.ApplyRecoveryAsync(target, Recovery(renames: [("bad:file.txt", "bad_file.txt")]), CancellationToken.None));

      Assert.Contains("is checked out normally", ex.Message);
      Assert.Equal("put here by hand", File.ReadAllText(Path.Combine(target, "readme.txt")));
   }



   [Fact]
   public async Task NeverCheckedOut_WithAFileOfTheUsersWhereTheRepositoryHasOne_IsRefused_AndTheFileIsKept()
   {
      string source = CreateForgedRepo(_root, ("bad:file.txt", "bad"), ("keep.txt", "keep"));
      string target = await CloneExpectingInvalidPathsAsync(source, "pending-with-a-file");
      File.WriteAllText(Path.Combine(target, "keep.txt"), "mine, put here by hand");

      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.ApplyRecoveryAsync(target, Recovery(renames: [("bad:file.txt", "bad_file.txt")]), CancellationToken.None));

      Assert.Contains("'keep.txt' is already in this folder", ex.Message);
      Assert.Equal("mine, put here by hand", File.ReadAllText(Path.Combine(target, "keep.txt")));
      Assert.False(File.Exists(Path.Combine(target, "bad_file.txt")));
   }



   [Fact]
   public async Task NeverCheckedOut_WithAFileAnUnfinishedCheckoutLeft_IsResolved()
   {
      // What a checkout that was cut short leaves: a file with exactly the commit's content.
      string source = CreateForgedRepo(_root, ("bad:file.txt", "bad"), ("keep.txt", "keep"));
      string target = await CloneExpectingInvalidPathsAsync(source, "pending-with-its-own-file");
      File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");

      await _client.ApplyRecoveryAsync(target, Recovery(renames: [("bad:file.txt", "bad_file.txt")]), CancellationToken.None);

      Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
      Assert.Equal("bad", File.ReadAllText(Path.Combine(target, "bad_file.txt")));
   }



   [Fact]
   public async Task Managed_ACommitPutOnTheTrackingBranchByHand_IsNotTakenForOneOfOrigins()
   {
      (string source, string target) = await CreateManagedAsync("managed-hand-updated-ref");
      string mine = CommitInWorkingTree(target, "mine.txt", "my work");
      using(var repo = new Repository(target))
      {
         // Not a fetch: whatever wrote this entry, it does not say origin had the commit.
         repo.Refs.UpdateTarget(repo.Refs[repo.Head.TrackedBranch.CanonicalName], new ObjectId(mine), "");
      }
      RewriteHistory(source, ("bad:file.txt", "bad again"), ("keep.txt", "keep"));

      await Assert.ThrowsAsync<InvalidOperationException>(() => _client.PullAsync(target, Token, CancellationToken.None));

      Assert.Equal(mine, HeadSha(target));
   }



   // ---------------------------------------------------------------- helpers

   /// <summary>Removes what this clone remembers of where origin's branch was, as expiry does.</summary>
   private static void DeleteReflogOfTheTrackedBranch(string workdir)
   {
      using var repo = new Repository(workdir);
      string log = Path.Combine(repo.Info.Path, "logs", repo.Head.TrackedBranch.CanonicalName.Replace('/', Path.DirectorySeparatorChar));
      File.Delete(log);
   }



   /// <summary>A forged source with valid paths, and an ordinary (fully checked-out) clone of it.</summary>
   private async Task<(string Source, string Target)> CloneOrdinaryAsync(params (string Name, string Content)[] entries)
   {
      string source = CreateForgedRepo(_root, entries);
      string target = Path.Combine(_root, "clone-" + Guid.NewGuid().ToString("N")[..8]);
      await _client.CloneAsync(source, target, Token, null, CancellationToken.None);
      return (source, target);
   }



   /// <summary>A source with one invalid path and "keep.txt", and a clone of it resolved by renaming that path.</summary>
   private async Task<(string Source, string Target)> CreateManagedAsync(string name)
   {
      string source = CreateForgedRepo(_root, ("bad:file.txt", "bad"), ("keep.txt", "keep"));
      string target = await CloneExpectingInvalidPathsAsync(source, name);
      await _client.ApplyRecoveryAsync(target, Recovery(renames: [("bad:file.txt", "bad_file.txt")]), CancellationToken.None);
      return (source, target);
   }



   private async Task<string> CloneExpectingInvalidPathsAsync(string source, string name)
   {
      string target = Path.Combine(_root, name);
      await Assert.ThrowsAsync<InvalidRepositoryPathsException>(
          () => _client.CloneAsync(source, target, Token, null, CancellationToken.None));
      return target;
   }



   private Task PullExpectingInvalidPathsAsync(string target)
       => Assert.ThrowsAsync<InvalidRepositoryPathsException>(() => _client.PullAsync(target, Token, CancellationToken.None));



   /// <summary>
   /// Runs <paramref name="operation"/> while <paramref name="fileName"/> is held open
   /// by a reader that lets others read it but not replace it: everything that is
   /// checked before the tree is rewritten passes, and the rewrite itself fails when
   /// it reaches that file, after the point of no return.
   /// </summary>
   private static async Task InterruptAsync(string target, string fileName, Func<Task> operation)
   {
      using var held = new FileStream(Path.Combine(target, fileName), FileMode.Open, FileAccess.Read, FileShare.Read);
      await Assert.ThrowsAnyAsync<IOException>(operation);
   }



   private static PathRecovery Recovery((string From, string To)[]? renames = null, string[]? skips = null)
       => new(
           (renames ?? []).ToDictionary(r => r.From, r => r.To, StringComparer.Ordinal),
           new HashSet<string>(skips ?? [], StringComparer.Ordinal));



   /// <summary>Adds a commit that removes one entry and writes (or overwrites) another.</summary>
   private static void ReplaceEntry(string workdir, string remove, (string Name, string Content) add)
   {
      using var repo = new Repository(workdir);
      Commit head = repo.Head.Tip;
      TreeDefinition definition = TreeDefinition.From(head.Tree).Remove(remove);
      definition.Add(add.Name, NewBlob(repo, add.Content), Mode.NonExecutableFile);
      Signature signature = MakeSignature();
      Commit commit = repo.ObjectDatabase.CreateCommit(
          signature, signature, "forged change", repo.ObjectDatabase.CreateTree(definition), [head], prettifyMessage: false);
      repo.Refs.UpdateTarget(repo.Refs.Head.ResolveToDirectReference(), commit.Id);
   }



   /// <summary>The first commit of a repository that has none yet; returns its SHA.</summary>
   private static string FirstForgedCommit(string workdir, params (string Name, string Content)[] entries)
   {
      using var repo = new Repository(workdir);
      Commit commit = NewRootCommit(repo, entries);
      repo.Refs.Add(repo.Refs.Head.TargetIdentifier, commit.Id);
      return commit.Sha;
   }



   /// <summary>Replaces the branch's history with a single new root commit, as a force-push does; returns its SHA.</summary>
   private static string RewriteHistory(string workdir, params (string Name, string Content)[] entries)
   {
      using var repo = new Repository(workdir);
      Commit commit = NewRootCommit(repo, entries);
      repo.Refs.UpdateTarget(repo.Refs.Head.ResolveToDirectReference(), commit.Id);
      return commit.Sha;
   }



   private static Commit NewRootCommit(Repository repo, (string Name, string Content)[] entries)
   {
      var definition = new TreeDefinition();
      foreach((string name, string content) in entries)
      {
         definition.Add(name, NewBlob(repo, content), Mode.NonExecutableFile);
      }
      Signature signature = MakeSignature();
      return repo.ObjectDatabase.CreateCommit(
          signature, signature, "forged root", repo.ObjectDatabase.CreateTree(definition), [], prettifyMessage: false);
   }



   private static Blob NewBlob(Repository repo, string content)
       => repo.ObjectDatabase.CreateBlob(new MemoryStream(Encoding.UTF8.GetBytes(content)));



   /// <summary>An ordinary commit made in the working tree, the way a person would.</summary>
   private static string CommitInWorkingTree(string workdir, string fileName, string content)
   {
      using var repo = new Repository(workdir);
      File.WriteAllText(Path.Combine(workdir, fileName), content);
      Commands.Stage(repo, fileName);
      Signature signature = MakeSignature();
      return repo.Commit("local work", signature, signature).Sha;
   }



   /// <summary>Every file of the working tree (not of .git) with its content.</summary>
   private static Dictionary<string, string> ReadAllFiles(string workdir)
       => Directory.EnumerateFiles(workdir, "*", SearchOption.AllDirectories)
           .Select(f => Path.GetRelativePath(workdir, f))
           .Where(f => !f.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
           .ToDictionary(f => f, f => File.ReadAllText(Path.Combine(workdir, f)), StringComparer.Ordinal);



   /// <summary>Without attributes, this setting writes text files with CRLF line endings.</summary>
   private static void UseCrLfByDefault(string repoPath)
   {
      using var repo = new Repository(repoPath);
      repo.Config.Set("core.autocrlf", "true", ConfigurationLevel.Local);
   }



   private static bool IsFlagSet(string repoPath, string key)
   {
      using var repo = new Repository(repoPath);
      return repo.Config.Get<bool>(key, ConfigurationLevel.Local)?.Value == true;
   }



   private static void UnsetFlag(string repoPath, string key)
   {
      using var repo = new Repository(repoPath);
      repo.Config.Unset(key, ConfigurationLevel.Local);
   }



   private static string RecoveryFilePath(string repoPath)
   {
      using var repo = new Repository(repoPath);
      return Path.Combine(repo.Info.Path, "gclo-recovery.json");
   }



   /// <summary>The commit the recovery file says is on disk; null while an apply is unfinished.</summary>
   private static string? MaterializedTip(string repoPath)
       => JsonNode.Parse(File.ReadAllText(RecoveryFilePath(repoPath)))!["MaterializedTip"]?.GetValue<string>();
}
