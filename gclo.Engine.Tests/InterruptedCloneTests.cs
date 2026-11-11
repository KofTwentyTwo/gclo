/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using LibGit2Sharp;
using static gclo.Engine.Tests.GitTestHelpers;


namespace gclo.Engine.Tests;


/// <summary>
/// Clones that did not get to their first checkout (an origin that was empty, a clone
/// that was cut off) and operations that are still ending when the next one starts:
/// the next sync completes what is missing, replaces nothing that is not gclo's, and
/// never works in a folder that another operation still holds.
/// </summary>
public sealed class InterruptedCloneTests : IDisposable
{
   private const string Token = "test-token";

   /// <summary>A branch name that is nobody's <c>init.defaultBranch</c>: a new clone's HEAD never names it.</summary>
   private const string OtherBranch = "not-the-branch-a-clone-starts-with";

   private readonly string _root = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

   private readonly LibGit2GitClient _client = new();



   public InterruptedCloneTests() => Directory.CreateDirectory(_root);



   public void Dispose() => TryDeleteDirectory(_root);



   private string NewPath(string name) => Path.Combine(_root, name);



   /// <summary>An empty bare origin and a clone of it, whose HEAD names a branch without commits.</summary>
   private async Task<(string Origin, string Clone)> CloneAnEmptyOriginAsync()
   {
      string origin = NewPath("origin.git");
      Repository.Init(origin, isBare: true);
      string clone = NewPath("clone");
      await _client.CloneAsync(origin, clone, Token, null, CancellationToken.None);
      using var repo = new Repository(clone);
      Assert.True(repo.Info.IsHeadUnborn);
      return (origin, clone);
   }



   /// <summary>
   /// Gives the origin its first commit through another working copy, on
   /// <paramref name="branch"/> (the branch a new clone starts with when null), and
   /// returns the commit's id.
   /// </summary>
   private async Task<string> SeedAsync(string origin, string? branch, params (string Path, string Content)[] files)
   {
      string seeder = NewPath("seeder-" + Guid.NewGuid().ToString("N")[..8]);
      await _client.CloneAsync(origin, seeder, Token, null, CancellationToken.None);
      using var repo = new Repository(seeder);
      if(branch is not null)
      {
         repo.Refs.UpdateTarget("HEAD", "refs/heads/" + branch);
      }
      foreach((string path, string content) in files)
      {
         string full = Path.Combine(seeder, path.Replace('/', Path.DirectorySeparatorChar));
         Directory.CreateDirectory(Path.GetDirectoryName(full)!);
         File.WriteAllText(full, content);
         Commands.Stage(repo, path);
      }
      Signature who = MakeSignature();
      Commit commit = repo.Commit("first commit", who, who);
      repo.Network.Push(repo.Network.Remotes["origin"], repo.Head.CanonicalName, new PushOptions());
      if(branch is not null)
      {
         using var bare = new Repository(origin);
         bare.Refs.UpdateTarget("HEAD", "refs/heads/" + branch); // origin's default branch
      }
      return commit.Sha;
   }



   /// <summary>The repository's own configuration, as "key=value" lines in a stable order.</summary>
   private static string[] LocalConfiguration(string workdir)
   {
      using var repo = new Repository(workdir);
      return [.. repo.Config.Where(entry => entry.Level == ConfigurationLevel.Local).Select(entry => $"{entry.Key}={entry.Value}").Order(StringComparer.Ordinal)];
   }



   private static bool IsCheckoutPending(string workdir)
   {
      using var repo = new Repository(workdir);
      return repo.Config.Get<bool>("gclo.checkoutpending", ConfigurationLevel.Local)?.Value == true;
   }



   // ---------------------------------------------------------------- the first checkout of an empty clone

   [Fact]
   public async Task AnEmptyClone_WhoseOriginStartedOnAnotherBranch_GetsOriginsDefaultBranch()
   {
      (string origin, string clone) = await CloneAnEmptyOriginAsync();
      string sha = await SeedAsync(origin, OtherBranch, ("readme.txt", "from the other branch"));

      await _client.PullAsync(clone, Token, CancellationToken.None);

      using var repo = new Repository(clone);
      Assert.False(repo.Info.IsHeadUnborn);
      Assert.Equal(OtherBranch, repo.Head.FriendlyName);
      Assert.Equal(sha, repo.Head.Tip.Sha);
      Assert.Equal("origin/" + OtherBranch, repo.Head.TrackedBranch?.FriendlyName);
      Assert.Equal("from the other branch", File.ReadAllText(Path.Combine(clone, "readme.txt")));
      Assert.False(IsCheckoutPending(clone));
   }



   [Theory]
   [InlineData("readme.txt", false, "'readme.txt' is already in this folder")] // a file where the repository has a file
   [InlineData("README.TXT", false, "'readme.txt' is already in this folder")] // the same file to Windows, whatever its case
   [InlineData("readme.txt", true, "'readme.txt' is a folder with files in it")] // a folder where the repository has a file
   [InlineData("docs", false, "'docs' is a file in this folder, and the repository has a folder of that name")] // a file where the repository needs a folder
   public async Task WhatIsAlreadyInTheFolder_WhereTheRepositoryHasSomething_IsNotReplaced(string inTheWay, bool asFolder, string said)
   {
      (string origin, string clone) = await CloneAnEmptyOriginAsync();
      string mine = Path.Combine(clone, inTheWay);
      if(asFolder)
      {
         Directory.CreateDirectory(mine);
         File.WriteAllText(Path.Combine(mine, "kept.txt"), "mine");
      }
      else
      {
         File.WriteAllText(mine, "mine");
      }
      await SeedAsync(origin, null, ("readme.txt", "theirs"), ("docs/guide.txt", "theirs"));
      string[] configurationBefore = LocalConfiguration(clone);

      InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.PullAsync(clone, Token, CancellationToken.None));

      Assert.Contains(said, refused.Message, StringComparison.Ordinal);
      Assert.Equal(configurationBefore, LocalConfiguration(clone)); // nothing was written: no marker, no upstream
      Assert.Equal("mine", File.ReadAllText(asFolder ? Path.Combine(mine, "kept.txt") : mine));
      using var repo = new Repository(clone);
      Assert.True(repo.Info.IsHeadUnborn); // nothing was started: no branch, no marker, no upstream written
      Assert.False(IsCheckoutPending(clone));
      Assert.False(File.Exists(Path.Combine(clone, "docs", "guide.txt")));
   }



   [Fact]
   public async Task AFileOfTheUsers_ThatTheRepositoryDoesNotHave_StaysBesideTheCheckout()
   {
      (string origin, string clone) = await CloneAnEmptyOriginAsync();
      File.WriteAllText(Path.Combine(clone, "notes.txt"), "mine");
      await SeedAsync(origin, null, ("readme.txt", "theirs"));

      await _client.PullAsync(clone, Token, CancellationToken.None);

      Assert.Equal("theirs", File.ReadAllText(Path.Combine(clone, "readme.txt")));
      Assert.Equal("mine", File.ReadAllText(Path.Combine(clone, "notes.txt")));
   }



   [Fact]
   public async Task AFirstCheckoutThatWasCutOff_IsCompletedByTheNextSync_NotReportedAsUpToDate()
   {
      (string origin, string clone) = await CloneAnEmptyOriginAsync();
      string sha = await SeedAsync(origin, null, ("readme.txt", "theirs"));

      // Cut off once the branch exists and before a file is written.
      using var cts = new CancellationTokenSource();
      _client.BeforeFirstCheckoutForTesting = cts.Cancel;
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _client.PullAsync(clone, Token, cts.Token));
      _client.BeforeFirstCheckoutForTesting = null;
      using(var cutOff = new Repository(clone))
      {
         Assert.False(cutOff.Info.IsHeadUnborn); // it looks like any clone now
      }

      Assert.False(File.Exists(Path.Combine(clone, "readme.txt")));
      Assert.True(IsCheckoutPending(clone)); // the folder says of itself that it is not finished
      using(var cutOffAgain = new Repository(clone))
      {
         // Its upstream was configured before the branch existed: no cut-off leaves a branch without one.
         Assert.Equal("origin/" + cutOffAgain.Head.FriendlyName, cutOffAgain.Head.TrackedBranch?.FriendlyName);
      }

      await _client.PullAsync(clone, Token, CancellationToken.None);

      Assert.Equal("theirs", File.ReadAllText(Path.Combine(clone, "readme.txt")));
      Assert.False(IsCheckoutPending(clone));
      using var repo = new Repository(clone);
      Assert.Equal(sha, repo.Head.Tip.Sha);
   }



   [Fact]
   public async Task ABranchStartedByHand_WithFilesStaged_IsLeftAlone_WhenOriginDoesNotHaveThatBranch()
   {
      (string origin, string clone) = await CloneAnEmptyOriginAsync();
      File.WriteAllText(Path.Combine(clone, "draft.txt"), "mine");
      using(var repo = new Repository(clone))
      {
         Commands.Stage(repo, "draft.txt");
      }
      await SeedAsync(origin, OtherBranch, ("readme.txt", "from the other branch"));

      await _client.PullAsync(clone, Token, CancellationToken.None); // fetches; checks nothing out

      using var after = new Repository(clone);
      Assert.True(after.Info.IsHeadUnborn);
      Assert.False(string.Equals(OtherBranch, after.Head.FriendlyName, StringComparison.Ordinal));
      Assert.Equal(1, after.Index.Count);
      Assert.False(File.Exists(Path.Combine(clone, "readme.txt")));
      Assert.Equal("mine", File.ReadAllText(Path.Combine(clone, "draft.txt")));
   }



   [Fact]
   public async Task AnOriginThatNamesNoDefaultBranch_LeavesTheEmptyCloneAsItIs()
   {
      (string origin, string clone) = await CloneAnEmptyOriginAsync();
      await SeedAsync(origin, OtherBranch, ("readme.txt", "from the other branch"));
      using(var bare = new Repository(origin))
      {
         bare.Refs.UpdateTarget("HEAD", "refs/heads/does-not-exist"); // HEAD names a branch that is not there
      }

      await _client.PullAsync(clone, Token, CancellationToken.None);

      using var after = new Repository(clone);
      Assert.True(after.Info.IsHeadUnborn);
      Assert.False(File.Exists(Path.Combine(clone, "readme.txt")));
   }



   // ---------------------------------------------------------------- one operation per folder

   [Fact]
   public async Task WhileAnOperationStillWorksInAFolder_AnotherOneThere_IsRefused_AndWorksOnceTheFirstHasEnded()
   {
      (string origin, _) = await CloneAnEmptyOriginAsync();
      await SeedAsync(origin, null, ("readme.txt", "theirs"));
      string target = NewPath("busy");
      var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      using var letGo = new ManualResetEventSlim();
      _client.AfterFetchForTesting = () =>
      {
         entered.SetResult();
         letGo.Wait(TimeSpan.FromSeconds(30));
      };

      Task clone = _client.CloneAsync(origin, target, Token, null, CancellationToken.None);
      await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

      // Another client, as another workspace of the same process would be; and the
      // same folder however it is written.
      var other = new LibGit2GitClient();
      InvalidOperationException pull = await Assert.ThrowsAsync<InvalidOperationException>(
          () => other.FetchAndPullAsync(origin, target, Token, CancellationToken.None));
      InvalidOperationException again = await Assert.ThrowsAsync<InvalidOperationException>(
          () => other.CloneAsync(origin, target + Path.DirectorySeparatorChar, Token, null, CancellationToken.None));
      Assert.Contains("Another git operation of gclo is still working in this folder", pull.Message, StringComparison.Ordinal);
      Assert.Contains("Sync again when it has finished", again.Message, StringComparison.Ordinal);

      letGo.Set();
      await clone;

      Assert.Equal("theirs", File.ReadAllText(Path.Combine(target, "readme.txt"))); // the first one was not disturbed
      await other.FetchAndPullAsync(origin, target, Token, CancellationToken.None); // and the folder is free again
   }



   // ---------------------------------------------------------------- a clone without an index

   [Fact]
   public async Task ACloneWhoseIndexIsGone_SaysSo_AndHowToRepairIt_InsteadOfCountingConflicts()
   {
      (string origin, _) = await CloneAnEmptyOriginAsync();
      await SeedAsync(origin, null, ("readme.txt", "one"));
      string target = NewPath("no-index");
      await _client.CloneAsync(origin, target, Token, null, CancellationToken.None);
      File.Delete(Path.Combine(target, ".git", "index"));

      // Origin moves on: the next sync has a file to update.
      string second = NewPath("second");
      await _client.CloneAsync(origin, second, Token, null, CancellationToken.None);
      using(var repo = new Repository(second))
      {
         File.WriteAllText(Path.Combine(second, "readme.txt"), "two");
         Commands.Stage(repo, "readme.txt");
         Signature who = MakeSignature();
         repo.Commit("second commit", who, who);
         repo.Network.Push(repo.Network.Remotes["origin"], repo.Head.CanonicalName, new PushOptions());
      }

      InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.FetchAndPullAsync(origin, target, Token, CancellationToken.None));

      Assert.Contains("has no index", failure.Message, StringComparison.Ordinal);
      Assert.Contains("reset", failure.Message, StringComparison.Ordinal);
      Assert.Equal("one", File.ReadAllText(Path.Combine(target, "readme.txt"))); // nothing was changed
   }



   // ---------------------------------------------------------------- what is not gclo's stays (review of #175)

   /// <summary>An empty clone whose first checkout was cut off once its branch existed and before a file was written.</summary>
   private async Task<(string Origin, string Clone)> ACloneWhoseFirstCheckoutWasCutOffAsync(params (string Path, string Content)[] files)
   {
      (string origin, string clone) = await CloneAnEmptyOriginAsync();
      await SeedAsync(origin, null, files);
      using var cts = new CancellationTokenSource();
      _client.BeforeFirstCheckoutForTesting = cts.Cancel;
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _client.PullAsync(clone, Token, cts.Token));
      _client.BeforeFirstCheckoutForTesting = null;
      Assert.True(IsCheckoutPending(clone));
      return (origin, clone);
   }



   [Fact]
   public async Task AFileTheUserAdds_AfterACheckoutWasCutOff_IsNotReplacedByTheRetry_WhichStaysUnfinishedUntilItIsMoved()
   {
      (_, string clone) = await ACloneWhoseFirstCheckoutWasCutOffAsync(("readme.txt", "theirs"), ("docs/guide.txt", "theirs"));
      File.WriteAllText(Path.Combine(clone, "readme.txt"), "MINE");

      InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.PullAsync(clone, Token, CancellationToken.None));

      Assert.Contains("'readme.txt' is already in this folder", refused.Message, StringComparison.Ordinal);
      Assert.Equal("MINE", File.ReadAllText(Path.Combine(clone, "readme.txt")));
      Assert.False(File.Exists(Path.Combine(clone, "docs", "guide.txt"))); // nothing was written beside it either
      Assert.True(IsCheckoutPending(clone)); // and the folder is still not taken for finished

      File.Move(Path.Combine(clone, "readme.txt"), Path.Combine(clone, "mine.txt"));
      await _client.PullAsync(clone, Token, CancellationToken.None);

      Assert.Equal("theirs", File.ReadAllText(Path.Combine(clone, "readme.txt")));
      Assert.Equal("MINE", File.ReadAllText(Path.Combine(clone, "mine.txt")));
      Assert.False(IsCheckoutPending(clone));
   }



   [Fact]
   public async Task AFileOfTheUsers_ThatOriginStartsToHave_AfterACheckoutWasCutOff_IsNotReplaced()
   {
      (string origin, string clone) = await CloneAnEmptyOriginAsync();
      File.WriteAllText(Path.Combine(clone, "notes.txt"), "mine"); // there from the start; the first commit does not have it
      await SeedAsync(origin, null, ("readme.txt", "theirs"));
      using var cts = new CancellationTokenSource();
      _client.BeforeFirstCheckoutForTesting = cts.Cancel;
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _client.PullAsync(clone, Token, cts.Token));
      _client.BeforeFirstCheckoutForTesting = null;

      // Origin moves on and now has a file of that name.
      string second = NewPath("second");
      await _client.CloneAsync(origin, second, Token, null, CancellationToken.None);
      using(var repo = new Repository(second))
      {
         File.WriteAllText(Path.Combine(second, "notes.txt"), "theirs");
         Commands.Stage(repo, "notes.txt");
         Signature who = MakeSignature();
         repo.Commit("second commit", who, who);
         repo.Network.Push(repo.Network.Remotes["origin"], repo.Head.CanonicalName, new PushOptions());
      }

      InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.PullAsync(clone, Token, CancellationToken.None));

      // The checkout that was cut off is finished at its own commit, which has no
      // such file; what origin got since is an ordinary update, and that one does
      // not replace a file that was added here.
      Assert.Contains("The update would replace files in this folder that are not as git recorded them", refused.Message, StringComparison.Ordinal);
      Assert.Equal("mine", File.ReadAllText(Path.Combine(clone, "notes.txt")));
      Assert.Equal("theirs", File.ReadAllText(Path.Combine(clone, "readme.txt")));
      Assert.False(IsCheckoutPending(clone));
   }



   [Fact]
   public async Task TheFileACutOffCheckoutWasWriting_IsFinishedByTheRetry()
   {
      (_, string clone) = await ACloneWhoseFirstCheckoutWasCutOffAsync(("readme.txt", "theirs, all of it"), ("done.txt", "written"));
      File.WriteAllText(Path.Combine(clone, "done.txt"), "written"); // one file had been written in full
      File.WriteAllText(Path.Combine(clone, "readme.txt"), "theirs, a"); // and this one was cut off in the middle

      await _client.PullAsync(clone, Token, CancellationToken.None);

      Assert.Equal("theirs, all of it", File.ReadAllText(Path.Combine(clone, "readme.txt")));
      Assert.Equal("written", File.ReadAllText(Path.Combine(clone, "done.txt")));
      Assert.False(IsCheckoutPending(clone));
   }



   [Fact]
   public async Task ACutOffThatLeftTheMarkerButNoBranchYet_StartsOver_InsteadOfReportingAnEmptyFolderAsDone()
   {
      (string origin, string clone) = await CloneAnEmptyOriginAsync();
      await SeedAsync(origin, null, ("readme.txt", "theirs"));
      using(var repo = new Repository(clone))
      {
         repo.Config.Set("gclo.checkoutpending", true, ConfigurationLevel.Local); // the process died right after this
      }

      await _client.PullAsync(clone, Token, CancellationToken.None);

      Assert.Equal("theirs", File.ReadAllText(Path.Combine(clone, "readme.txt")));
      Assert.False(IsCheckoutPending(clone));
   }



   [Fact]
   public async Task TheFolderIsMarkedUnfinished_BeforeItsBranchIsCreated()
   {
      (string origin, string clone) = await CloneAnEmptyOriginAsync();
      await SeedAsync(origin, null, ("readme.txt", "theirs"));
      string branch;
      using(var repo = new Repository(clone))
      {
         branch = repo.Refs.Head.TargetIdentifier["refs/heads/".Length..];
      }

      // Something that makes creating the branch fail: another git holds its lock.
      string heads = Directory.CreateDirectory(Path.Combine(clone, ".git", "refs", "heads")).FullName;
      string held = Path.Combine(heads, branch + ".lock");
      File.WriteAllText(held, "");

      await Assert.ThrowsAnyAsync<Exception>(() => _client.PullAsync(clone, Token, CancellationToken.None));

      // Whatever stopped it, the folder already says that it is not finished, and
      // the branch already has its upstream: no cut-off leaves one without.
      Assert.True(IsCheckoutPending(clone));
      Assert.False(File.Exists(Path.Combine(clone, "readme.txt")));
      using(var stopped = new Repository(clone))
      {
         Assert.True(stopped.Info.IsHeadUnborn);
         Assert.Equal("origin", stopped.Config.Get<string>($"branch.{branch}.remote", ConfigurationLevel.Local)?.Value);
         Assert.Equal("refs/heads/" + branch, stopped.Config.Get<string>($"branch.{branch}.merge", ConfigurationLevel.Local)?.Value);
      }

      File.Delete(held);
      await _client.PullAsync(clone, Token, CancellationToken.None);

      Assert.Equal("theirs", File.ReadAllText(Path.Combine(clone, "readme.txt")));
      Assert.False(IsCheckoutPending(clone));
   }



   [Fact]
   public async Task ABranchSomebodyStarted_InACloneThatHasBranches_IsTheirs_HoweverOftenItIsSynced()
   {
      (string origin, _) = await CloneAnEmptyOriginAsync();
      await SeedAsync(origin, null, ("readme.txt", "theirs"), ("index.html", "theirs"));
      string clone = NewPath("ordinary");
      await _client.CloneAsync(origin, clone, Token, null, CancellationToken.None);

      // 'git switch --orphan pages': HEAD names a branch without commits, the index
      // and the files of the branch that was left are gone.
      string before;
      using(var repo = new Repository(clone))
      {
         before = repo.Head.FriendlyName;
         repo.Refs.UpdateTarget("HEAD", "refs/heads/pages");
         repo.Index.Clear();
         repo.Index.Write();
      }
      File.Delete(Path.Combine(clone, "readme.txt"));
      File.Delete(Path.Combine(clone, "index.html"));
      File.WriteAllText(Path.Combine(clone, "page.html"), "mine");

      await _client.PullAsync(clone, Token, CancellationToken.None);
      File.WriteAllText(Path.Combine(clone, "readme.txt"), "mine too");
      await _client.PullAsync(clone, Token, CancellationToken.None);

      using var after = new Repository(clone);
      Assert.True(after.Info.IsHeadUnborn);
      Assert.Equal("pages", after.Head.FriendlyName);
      Assert.NotNull(after.Branches[before]); // the branch that was left is still there, untouched
      Assert.Equal(0, after.Index.Count);
      Assert.Equal("mine", File.ReadAllText(Path.Combine(clone, "page.html")));
      Assert.Equal("mine too", File.ReadAllText(Path.Combine(clone, "readme.txt")));
      Assert.False(File.Exists(Path.Combine(clone, "index.html")));
      Assert.False(IsCheckoutPending(clone));
   }



   [Theory]
   [InlineData(true)] // a folder with something in it
   [InlineData(false)] // a file
   public async Task WhatIsWhereTheRepositoryHasASubmodule_IsNotReplaced_AndAnEmptyFolderThereIsFine(bool asFolder)
   {
      (string origin, string clone) = await CloneAnEmptyOriginAsync();

      // Origin's first commit: one file and a submodule entry 'sub'.
      string seeder = NewPath("seeder-with-submodule");
      await _client.CloneAsync(origin, seeder, Token, null, CancellationToken.None);
      using(var repo = new Repository(seeder))
      {
         Blob readme = repo.ObjectDatabase.CreateBlob(new MemoryStream("theirs"u8.ToArray()));
         var definition = new TreeDefinition();
         definition.Add("readme.txt", readme, Mode.NonExecutableFile);
         definition.AddGitLink("sub", new ObjectId("1234567890123456789012345678901234567890"));
         Tree tree = repo.ObjectDatabase.CreateTree(definition);
         Signature who = MakeSignature();
         Commit commit = repo.ObjectDatabase.CreateCommit(who, who, "first commit", tree, [], prettifyMessage: false);
         repo.Refs.Add(repo.Refs.Head.TargetIdentifier, commit.Id);
         repo.Network.Push(repo.Network.Remotes["origin"], repo.Refs.Head.TargetIdentifier, new PushOptions());
      }

      string mine = Path.Combine(clone, "sub");
      if(asFolder)
      {
         Directory.CreateDirectory(mine);
         File.WriteAllText(Path.Combine(mine, "kept.txt"), "mine");
      }
      else
      {
         File.WriteAllText(mine, "mine");
      }

      InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.PullAsync(clone, Token, CancellationToken.None));

      Assert.Contains("'sub' is already in this folder, where the repository has a submodule", refused.Message, StringComparison.Ordinal);
      Assert.Equal("mine", File.ReadAllText(asFolder ? Path.Combine(mine, "kept.txt") : mine));
      Assert.False(File.Exists(Path.Combine(clone, "readme.txt")));

      // Out of the way, or an empty folder in its place: the checkout goes ahead.
      if(asFolder)
      {
         File.Delete(Path.Combine(mine, "kept.txt"));
      }
      else
      {
         File.Delete(mine);
      }
      await _client.PullAsync(clone, Token, CancellationToken.None);
      Assert.Equal("theirs", File.ReadAllText(Path.Combine(clone, "readme.txt")));
   }



   [Fact]
   public async Task ALinkWhereTheRepositoryNeedsAFolder_IsNotReplacedByARealFolder()
   {
      (string origin, string clone) = await CloneAnEmptyOriginAsync();
      await SeedAsync(origin, null, ("docs/guide.txt", "theirs"));
      string elsewhere = Directory.CreateDirectory(NewPath("elsewhere")).FullName;
      string link = Path.Combine(clone, "docs");
      WorkingTreeTests.CreateDirectoryLink(link, elsewhere);
      try
      {
         await Assert.ThrowsAnyAsync<Exception>(() => _client.PullAsync(clone, Token, CancellationToken.None));

         Assert.NotNull(new DirectoryInfo(link).LinkTarget); // still the link it was
         Assert.Empty(Directory.GetFileSystemEntries(elsewhere)); // and nothing was written through it
         using var repo = new Repository(clone);
         Assert.True(repo.Info.IsHeadUnborn);
      }
      finally
      {
         WorkingTreeTests.RemoveLink(link);
      }
   }



   // ---------------------------------------------------------------- one operation per folder, however it is written

   [Fact]
   public async Task AFolderThatIsBusy_IsBusyForARecoveryToo_AndUnderEverySpellingOfItsName()
   {
      (string origin, _) = await CloneAnEmptyOriginAsync();
      await SeedAsync(origin, null, ("readme.txt", "theirs"));
      string target = NewPath("busy-too");
      var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      using var letGo = new ManualResetEventSlim();
      _client.AfterFetchForTesting = () =>
      {
         entered.SetResult();
         letGo.Wait(TimeSpan.FromSeconds(30));
      };
      Task clone = _client.CloneAsync(origin, target, Token, null, CancellationToken.None);
      await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
      var other = new LibGit2GitClient();
      var nothing = new PathRecovery(new Dictionary<string, string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

      InvalidOperationException recovery = await Assert.ThrowsAsync<InvalidOperationException>(
          () => other.ApplyRecoveryAsync(target, nothing, CancellationToken.None));
      Assert.Contains("still working in this folder", recovery.Message, StringComparison.Ordinal);
      if(OperatingSystem.IsWindows())
      {
         // One folder to Windows: another case, and the long-path spelling.
         await Assert.ThrowsAsync<InvalidOperationException>(
             () => other.FetchAndPullAsync(origin, target.ToUpperInvariant(), Token, CancellationToken.None));
         foreach(string prefix in (string[])[@"\\?\", @"\\.\", @"\??\"])
         {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => other.FetchAndPullAsync(origin, prefix + target, Token, CancellationToken.None));
         }
      }

      letGo.Set();
      await clone;
   }



   // ---------------------------------------------------------------- the missing-index message is for a missing index only

   [Fact]
   public async Task AnOrdinaryConflict_WithTheIndexInPlace_IsNotBlamedOnAMissingIndex()
   {
      (string origin, _) = await CloneAnEmptyOriginAsync();
      await SeedAsync(origin, null, ("readme.txt", "one"));
      string target = NewPath("edited");
      await _client.CloneAsync(origin, target, Token, null, CancellationToken.None);
      File.WriteAllText(Path.Combine(target, "readme.txt"), "edited here"); // an edit that is not committed

      string second = NewPath("second");
      await _client.CloneAsync(origin, second, Token, null, CancellationToken.None);
      using(var repo = new Repository(second))
      {
         File.WriteAllText(Path.Combine(second, "readme.txt"), "two");
         Commands.Stage(repo, "readme.txt");
         Signature who = MakeSignature();
         repo.Commit("second commit", who, who);
         repo.Network.Push(repo.Network.Remotes["origin"], repo.Head.CanonicalName, new PushOptions());
      }

      Exception failure = await Assert.ThrowsAnyAsync<Exception>(
          () => _client.FetchAndPullAsync(origin, target, Token, CancellationToken.None));

      Assert.DoesNotContain("has no index", failure.Message, StringComparison.Ordinal);
      Assert.Equal("edited here", File.ReadAllText(Path.Combine(target, "readme.txt")));
   }



   // ---------------------------------------------------------------- review of #175, second round

   [Fact]
   public async Task ACheckoutCutOffHalfWay_WithLineEndingsBeingConverted_IsStillCompleted()
   {
      // The tree brings its own line-ending rules, and the user's configuration has
      // others: the files written before '.gitattributes' was on disk follow the
      // configuration alone. They are gclo's all the same.
      (string origin, _) = await CloneAnEmptyOriginAsync();
      await SeedAsync(
          origin, null,
          (".gitattributes", "* text=auto eol=lf" + (char)10),
          ("a.txt", "one" + (char)10 + "two" + (char)10),
          ("zzz.txt", "last" + (char)10));
      string target = Directory.CreateDirectory(NewPath("cut-off-half-way")).FullName; // it exists: a failed clone is then kept
      FileStream? held = null;
      _client.AfterFetchForTesting = () =>
      {
         using(var fetched = new Repository(target))
         {
            fetched.Config.Set("core.autocrlf", true, ConfigurationLevel.Local);
         }
         // Another program has the last file open: the checkout fails when it gets there.
         held = new FileStream(Path.Combine(target, "zzz.txt"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
      };
      try
      {
         await Assert.ThrowsAnyAsync<Exception>(() => _client.CloneAsync(origin, target, Token, null, CancellationToken.None));
      }
      finally
      {
         held?.Dispose();
         _client.AfterFetchForTesting = null;
      }

      Assert.True(IsCheckoutPending(target));
      Assert.True(File.Exists(Path.Combine(target, ".gitattributes"))); // written before the failure
      using(var cutOff = new Repository(target))
      {
         Assert.Equal(0, cutOff.Index.Count); // git writes the index last: a checkout that was cut off leaves none
      }

      await _client.FetchAndPullAsync(origin, target, Token, CancellationToken.None);

      Assert.False(IsCheckoutPending(target));
      Assert.Equal("last", File.ReadAllText(Path.Combine(target, "zzz.txt")).TrimEnd());
      Assert.Equal(["one", "two"], File.ReadAllLines(Path.Combine(target, "a.txt")));
   }



   [Theory]
   [InlineData(150_001, null, true)] // cut off in the middle of a large file, with other line endings
   [InlineData(0, null, true)] // cut off before its first byte
   [InlineData(150_001, 120_000, false)] // the same, with one byte that is not the commit's
   public async Task WhatACutOffCheckoutLeft_IsRecognisedByItsContent_LineEndingsAside(int length, int? changedAt, bool isOurs)
   {
      string big = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"line {i:D6}" + (char)10)); // more than two buffers
      (_, string clone) = await ACloneWhoseFirstCheckoutWasCutOffAsync(("big.txt", big));
      char[] onDisk = big.Replace(((char)10).ToString(), "" + (char)13 + (char)10, StringComparison.Ordinal)[..length].ToCharArray();
      if(changedAt is int at)
      {
         onDisk[at] = onDisk[at] == 'x' ? 'y' : 'x';
      }
      File.WriteAllText(Path.Combine(clone, "big.txt"), new string(onDisk));

      if(isOurs)
      {
         await _client.PullAsync(clone, Token, CancellationToken.None);
         Assert.Equal(
             big,
             File.ReadAllText(Path.Combine(clone, "big.txt")).Replace("" + (char)13, "", StringComparison.Ordinal));
         Assert.False(IsCheckoutPending(clone));
      }
      else
      {
         InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
             () => _client.PullAsync(clone, Token, CancellationToken.None));
         Assert.Contains("'big.txt' is already in this folder", refused.Message, StringComparison.Ordinal);
         Assert.Equal(new string(onDisk), File.ReadAllText(Path.Combine(clone, "big.txt")));
      }
   }



   [Fact]
   public async Task AFileLongerThanTheCommits_IsNotACutOffWrite()
   {
      (_, string clone) = await ACloneWhoseFirstCheckoutWasCutOffAsync(("readme.txt", "theirs"));
      File.WriteAllText(Path.Combine(clone, "readme.txt"), "theirs, and what I added");

      await Assert.ThrowsAsync<InvalidOperationException>(() => _client.PullAsync(clone, Token, CancellationToken.None));

      Assert.Equal("theirs, and what I added", File.ReadAllText(Path.Combine(clone, "readme.txt")));
   }



   [Fact]
   public async Task AMarkedFolderThatHasAnIndex_IsAFinishedCheckout_AndIsUpdatedWithoutForce()
   {
      // The checkout reached its end and the marker could not be cleared any more;
      // or somebody finished it with git. Either way the tree is git's now.
      (string origin, _) = await CloneAnEmptyOriginAsync();
      await SeedAsync(origin, null, ("readme.txt", "one"), ("other.txt", "other"));
      string target = NewPath("finished-but-marked");
      await _client.CloneAsync(origin, target, Token, null, CancellationToken.None);
      using(var repo = new Repository(target))
      {
         repo.Config.Set("gclo.checkoutpending", true, ConfigurationLevel.Local);
      }
      File.WriteAllText(Path.Combine(target, "other.txt"), "edited here"); // an edit; a forced completion would undo it

      await _client.FetchAndPullAsync(origin, target, Token, CancellationToken.None);

      Assert.Equal("edited here", File.ReadAllText(Path.Combine(target, "other.txt")));
      Assert.False(IsCheckoutPending(target));
   }



   [Fact]
   public async Task AFileWhereASubmodulesParentFolderIsNeeded_IsNotReplaced()
   {
      (string origin, string clone) = await CloneAnEmptyOriginAsync();
      string seeder = NewPath("seeder-with-nested-submodule");
      await _client.CloneAsync(origin, seeder, Token, null, CancellationToken.None);
      using(var repo = new Repository(seeder))
      {
         Blob readme = repo.ObjectDatabase.CreateBlob(new MemoryStream("theirs"u8.ToArray()));
         var definition = new TreeDefinition();
         definition.Add("readme.txt", readme, Mode.NonExecutableFile);
         definition.AddGitLink("libs/sub", new ObjectId("1234567890123456789012345678901234567890"));
         Tree tree = repo.ObjectDatabase.CreateTree(definition);
         Signature who = MakeSignature();
         Commit commit = repo.ObjectDatabase.CreateCommit(who, who, "first commit", tree, [], prettifyMessage: false);
         repo.Refs.Add(repo.Refs.Head.TargetIdentifier, commit.Id);
         repo.Network.Push(repo.Network.Remotes["origin"], repo.Refs.Head.TargetIdentifier, new PushOptions());
      }
      File.WriteAllText(Path.Combine(clone, "libs"), "mine");

      InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.PullAsync(clone, Token, CancellationToken.None));

      Assert.Contains("'libs' is a file in this folder, and the repository has a folder of that name", refused.Message, StringComparison.Ordinal);
      Assert.Equal("mine", File.ReadAllText(Path.Combine(clone, "libs")));
   }



   [Theory]
   [InlineData(false, "'readme.txt' is a link in this folder")] // a link where the repository has a file
   [InlineData(true, "'readme.txt' is a folder that is not empty")] // a folder that holds nothing but a link
   public async Task ALinkWhereTheRepositoryHasAFile_IsNotRemoved(bool insideAFolder, string said)
   {
      (string origin, string clone) = await CloneAnEmptyOriginAsync();
      await SeedAsync(origin, null, ("readme.txt", "theirs"));
      string elsewhere = Directory.CreateDirectory(NewPath("elsewhere")).FullName;
      File.WriteAllText(Path.Combine(elsewhere, "kept.txt"), "mine");
      string link = insideAFolder
          ? Path.Combine(Directory.CreateDirectory(Path.Combine(clone, "readme.txt")).FullName, "j")
          : Path.Combine(clone, "readme.txt");
      WorkingTreeTests.CreateDirectoryLink(link, elsewhere);
      try
      {
         InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
             () => _client.PullAsync(clone, Token, CancellationToken.None));

         Assert.Contains(said, refused.Message, StringComparison.Ordinal);
         Assert.NotNull(new DirectoryInfo(link).LinkTarget);
         Assert.Equal("mine", File.ReadAllText(Path.Combine(elsewhere, "kept.txt")));
      }
      finally
      {
         WorkingTreeTests.RemoveLink(link);
      }
   }



   [Fact]
   public async Task AFolderSomebodyPutSomethingInto_KeepsTheBranchItsHeadNames_WhenOriginHasAnother()
   {
      (string origin, string clone) = await CloneAnEmptyOriginAsync();
      string before;
      using(var repo = new Repository(clone))
      {
         before = repo.Refs.Head.TargetIdentifier;
      }
      File.WriteAllText(Path.Combine(clone, "page.html"), "mine"); // not the untouched folder of an empty clone any more
      await SeedAsync(origin, OtherBranch, ("readme.txt", "from the other branch"));

      await _client.PullAsync(clone, Token, CancellationToken.None);

      using var after = new Repository(clone);
      Assert.True(after.Info.IsHeadUnborn);
      Assert.Equal(before, after.Refs.Head.TargetIdentifier);
      Assert.False(File.Exists(Path.Combine(clone, "readme.txt")));
      Assert.False(IsCheckoutPending(clone));
   }



   [Fact]
   public async Task ABranchSomebodyStarted_IsTheirs_AlsoWhenOriginHasABranchOfThatName()
   {
      (string origin, _) = await CloneAnEmptyOriginAsync();
      await SeedAsync(origin, null, ("readme.txt", "theirs"));
      string clone = NewPath("ordinary-with-a-new-branch");
      await _client.CloneAsync(origin, clone, Token, null, CancellationToken.None);

      // Origin gets a branch 'pages' of its own...
      string publisher = NewPath("publisher");
      await _client.CloneAsync(origin, publisher, Token, null, CancellationToken.None);
      using(var repo = new Repository(publisher))
      {
         Branch pages = repo.CreateBranch("pages");
         repo.Network.Push(repo.Network.Remotes["origin"], pages.CanonicalName, new PushOptions());
      }

      // ...and here somebody starts one of that name from nothing ('git switch --orphan pages').
      using(var repo = new Repository(clone))
      {
         repo.Refs.UpdateTarget("HEAD", "refs/heads/pages");
         repo.Index.Clear();
         repo.Index.Write();
      }
      File.Delete(Path.Combine(clone, "readme.txt"));
      File.WriteAllText(Path.Combine(clone, "page.html"), "mine");

      await _client.PullAsync(clone, Token, CancellationToken.None);

      using var after = new Repository(clone);
      Assert.True(after.Info.IsHeadUnborn); // still theirs: origin's 'pages' was fetched, not checked out
      Assert.Equal("pages", after.Head.FriendlyName);
      Assert.False(File.Exists(Path.Combine(clone, "readme.txt")));
      Assert.Equal("mine", File.ReadAllText(Path.Combine(clone, "page.html")));
   }



   // ---------------------------------------------------------------- review of #175, third round

   private const string LF = "\n";

   private const string CRLF = "\r\n";



   /// <summary>
   /// Adds a commit to the origin itself (no working copy in between), on the branch
   /// its HEAD names: the tree of the commit before it, changed by
   /// <paramref name="change"/>; with <paramref name="asANewRoot"/> a commit without
   /// a parent, which is what a rewritten history looks like from a clone.
   /// </summary>
   private static string CommitToOrigin(string origin, Func<Repository, TreeDefinition, TreeDefinition> change, bool asANewRoot = false)
   {
      using var repo = new Repository(origin);
      Commit? before = repo.Head.Tip;
      TreeDefinition definition = change(repo, before is null || asANewRoot ? new TreeDefinition() : TreeDefinition.From(before.Tree));
      Signature who = MakeSignature();
      Commit commit = repo.ObjectDatabase.CreateCommit(
          who, who, "written into origin", repo.ObjectDatabase.CreateTree(definition),
          before is null || asANewRoot ? [] : [before], prettifyMessage: false);
      if(before is null)
      {
         repo.Refs.Add(repo.Refs.Head.TargetIdentifier, commit.Id);
      }
      else
      {
         repo.Refs.UpdateTarget(repo.Refs.Head.ResolveToDirectReference(), commit.Id);
      }
      return commit.Sha;
   }



   private static Blob BlobOf(Repository repo, string content)
       => repo.ObjectDatabase.CreateBlob(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)));



   private static string? WrittenBefore(string workdir)
   {
      using var repo = new Repository(workdir);
      return repo.Config.Get<string>("gclo.checkoutwrittenbefore", ConfigurationLevel.Local)?.Value;
   }



   [Fact]
   public async Task OriginGetsAPathWindowsCannotCreate_AndLosesItAgain_TheUnfinishedCloneIsStillCompleted()
   {
      if(!OperatingSystem.IsWindows())
      {
         return; // the paths are Windows' to refuse
      }
      (string origin, string clone) = await ACloneWhoseFirstCheckoutWasCutOffAsync(("a.txt", "one"), ("b.txt", "b"));
      File.WriteAllText(Path.Combine(clone, "a.txt"), "one"); // what the cut-off checkout had written
      File.WriteAllText(Path.Combine(clone, "b.txt"), "b");
      string first = HeadSha(clone);

      CommitToOrigin(origin, (repo, tree) => tree.Add("bad:name.txt", BlobOf(repo, "bad"), Mode.NonExecutableFile));
      await Assert.ThrowsAsync<InvalidRepositoryPathsException>(() => _client.PullAsync(clone, Token, CancellationToken.None));
      Assert.True(IsCheckoutPending(clone));
      Assert.Equal(first, WrittenBefore(clone)); // the branch moved on; the commit whose files are on disk is remembered

      string third = CommitToOrigin(
          origin, (repo, tree) => tree.Remove("bad:name.txt").Add("a.txt", BlobOf(repo, "two"), Mode.NonExecutableFile));
      await _client.PullAsync(clone, Token, CancellationToken.None);

      Assert.Equal(third, HeadSha(clone));
      Assert.Equal("two", File.ReadAllText(Path.Combine(clone, "a.txt")));
      Assert.Equal("b", File.ReadAllText(Path.Combine(clone, "b.txt")));
      Assert.False(IsCheckoutPending(clone));
      Assert.Null(WrittenBefore(clone));
   }



   [Fact]
   public async Task OriginRewritesItsHistory_BeforeAnUnfinishedCloneIsSyncedAgain_AndTheCloneFollowsIt()
   {
      (string origin, string clone) = await ACloneWhoseFirstCheckoutWasCutOffAsync(("a.txt", "one"), ("b.txt", "b"));
      File.WriteAllText(Path.Combine(clone, "a.txt"), "one"); // what the cut-off checkout had written
      File.WriteAllText(Path.Combine(clone, "b.txt"), "b");
      string rewritten = CommitToOrigin(
          origin, (repo, tree) => tree.Add("a.txt", BlobOf(repo, "rewritten"), Mode.NonExecutableFile), asANewRoot: true);

      await _client.PullAsync(clone, Token, CancellationToken.None); // nobody committed here: nothing to push or reset

      Assert.Equal(rewritten, HeadSha(clone));
      Assert.Equal("rewritten", File.ReadAllText(Path.Combine(clone, "a.txt")));
      Assert.Equal("b", File.ReadAllText(Path.Combine(clone, "b.txt"))); // not in the new history: it stays, as a file git does not track
      Assert.False(IsCheckoutPending(clone));
      Assert.Null(WrittenBefore(clone));
   }



   /// <summary>
   /// A clone into a folder that exists, whose checkout fails when it gets to
   /// <c>zzz.txt</c> because another program has that file open: the files before it
   /// are written, the marker is set, and there is no index.
   /// </summary>
   private async Task<string> ACloneWhoseCheckoutFailedHalfWayAsync(string origin, string name)
   {
      string target = Directory.CreateDirectory(NewPath(name)).FullName;
      FileStream? held = null;
      _client.AfterFetchForTesting = () =>
          held = new FileStream(Path.Combine(target, "zzz.txt"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
      try
      {
         await Assert.ThrowsAnyAsync<Exception>(() => _client.CloneAsync(origin, target, Token, null, CancellationToken.None));
      }
      finally
      {
         held?.Dispose();
         _client.AfterFetchForTesting = null;
      }
      Assert.True(IsCheckoutPending(target));
      return target;
   }



   [Fact]
   public async Task AFileTheCheckoutWroteAnObjectIdInto_IsStillItsOwn()
   {
      // The 'ident' attribute: what is written is not what is stored.
      (string origin, _) = await CloneAnEmptyOriginAsync();
      CommitToOrigin(origin, (repo, tree) => tree
          .Add(".gitattributes", BlobOf(repo, "*.txt ident" + LF), Mode.NonExecutableFile)
          .Add("a.txt", BlobOf(repo, "$Id$" + LF + "hello" + LF), Mode.NonExecutableFile)
          .Add("zzz.txt", BlobOf(repo, "last" + LF), Mode.NonExecutableFile));
      string target = await ACloneWhoseCheckoutFailedHalfWayAsync(origin, "ident");
      Assert.Contains("$Id: ", File.ReadAllText(Path.Combine(target, "a.txt")), StringComparison.Ordinal);

      await _client.FetchAndPullAsync(origin, target, Token, CancellationToken.None);

      Assert.False(IsCheckoutPending(target));
      Assert.StartsWith("last", File.ReadAllText(Path.Combine(target, "zzz.txt")), StringComparison.Ordinal);
   }



   [Theory]
   [InlineData(false)] // 'git reset': the index names every file, the folder does not hold them all
   [InlineData(true)] // 'git add -A': the index names what is in the folder
   public async Task AnIndexSomebodysGitWrote_OverAnUnfinishedTree_DoesNotMakeTheTreeWhole(bool onlyWhatIsThere)
   {
      (string origin, _) = await CloneAnEmptyOriginAsync();
      CommitToOrigin(origin, (repo, tree) => tree
          .Add("a.txt", BlobOf(repo, "a"), Mode.NonExecutableFile)
          .Add("b.txt", BlobOf(repo, "b"), Mode.NonExecutableFile)
          .Add("zzz.txt", BlobOf(repo, "last"), Mode.NonExecutableFile));
      string target = await ACloneWhoseCheckoutFailedHalfWayAsync(origin, "then-git");
      File.Delete(Path.Combine(target, "zzz.txt"));
      using(var repo = new Repository(target))
      {
         if(onlyWhatIsThere)
         {
            Commands.Stage(repo, "*");
         }
         else
         {
            repo.Index.Replace(repo.Head.Tip);
            repo.Index.Write();
         }
         Assert.True(repo.Index.Count > 0);
      }

      await _client.FetchAndPullAsync(origin, target, Token, CancellationToken.None);

      Assert.Equal("last", File.ReadAllText(Path.Combine(target, "zzz.txt"))); // completed, not released with a file missing
      Assert.False(IsCheckoutPending(target));
   }



   [Theory]
   [InlineData("one" + LF + "tw")] // written with other line endings, and cut off
   [InlineData("one" + CRLF + "two" + CRLF)] // written as it is stored
   public async Task AFileStoredWithCarriageReturns_IsRecognisedTheSameWay(string onDisk)
   {
      (string origin, string clone) = await CloneAnEmptyOriginAsync();
      CommitToOrigin(origin, (repo, tree) => tree.Add("crlf.txt", BlobOf(repo, "one" + CRLF + "two" + CRLF), Mode.NonExecutableFile));
      using var cts = new CancellationTokenSource();
      _client.BeforeFirstCheckoutForTesting = cts.Cancel;
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _client.PullAsync(clone, Token, cts.Token));
      _client.BeforeFirstCheckoutForTesting = null;
      File.WriteAllText(Path.Combine(clone, "crlf.txt"), onDisk);

      await _client.PullAsync(clone, Token, CancellationToken.None);

      Assert.Equal(["one", "two"], File.ReadAllLines(Path.Combine(clone, "crlf.txt")));
      Assert.False(IsCheckoutPending(clone));
   }



   [Fact]
   public async Task ACompletionThatFailsHalfWay_LeavesTheFolderMarked()
   {
      (string origin, _) = await CloneAnEmptyOriginAsync();
      CommitToOrigin(origin, (repo, tree) => tree
          .Add("a.txt", BlobOf(repo, "a"), Mode.NonExecutableFile)
          .Add("zzz.txt", BlobOf(repo, "last"), Mode.NonExecutableFile));
      string target = await ACloneWhoseCheckoutFailedHalfWayAsync(origin, "twice");

      // The next sync is the completion, and it fails at the same file: it can be
      // read (so it is seen to be the checkout's own), not written.
      using(var held = new FileStream(Path.Combine(target, "zzz.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
      {
         await Assert.ThrowsAnyAsync<Exception>(() => _client.FetchAndPullAsync(origin, target, Token, CancellationToken.None));
      }
      Assert.True(IsCheckoutPending(target)); // not taken off before the tree is written

      await _client.FetchAndPullAsync(origin, target, Token, CancellationToken.None);
      Assert.Equal("last", File.ReadAllText(Path.Combine(target, "zzz.txt")));
      Assert.False(IsCheckoutPending(target));
   }



   [Theory]
   [InlineData(true, true)]
   [InlineData(true, false)]
   [InlineData(false, true)]
   [InlineData(false, false)] // a link of the user's, in a folder nothing was ever checked out into
   public async Task ALinkWhereTheRepositoryHasASymbolicLink_IsTheCheckoutsOwn_OnlyWhenItLeadsWhereTheRepositorysDoes(
       bool afterACutOff, bool leadsThere)
   {
      (string origin, string clone) = await CloneAnEmptyOriginAsync();
      string elsewhere = Directory.CreateDirectory(NewPath("elsewhere")).FullName;
      File.WriteAllText(Path.Combine(elsewhere, "kept.txt"), "mine");
      // A repository names the target of a link with '/'; a link on Windows has '\\'.
      string named = leadsThere ? elsewhere.Replace('\\', '/') : "readme.txt";
      CommitToOrigin(origin, (repo, tree) => tree
          .Add("readme.txt", BlobOf(repo, "theirs"), Mode.NonExecutableFile)
          .Add("alink", BlobOf(repo, named), Mode.SymbolicLink));
      using(var repo = new Repository(clone))
      {
         repo.Config.Set("core.symlinks", false, ConfigurationLevel.Local); // the link is then written as a file that names its target
      }
      if(afterACutOff)
      {
         await CutOffBeforeTheFirstFileAsync(clone);
      }
      string link = Path.Combine(clone, "alink");
      WorkingTreeTests.CreateDirectoryLink(link, elsewhere);
      try
      {
         if(leadsThere)
         {
            await _client.PullAsync(clone, Token, CancellationToken.None);

            Assert.Equal(named, File.ReadAllText(link));
            Assert.False(IsCheckoutPending(clone));
         }
         else
         {
            InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
                () => _client.PullAsync(clone, Token, CancellationToken.None));

            Assert.Contains("'alink' is a link", refused.Message, StringComparison.Ordinal);
            Assert.NotNull(new DirectoryInfo(link).LinkTarget);
            Assert.False(File.Exists(Path.Combine(clone, "readme.txt")));
         }
         Assert.Equal("mine", File.ReadAllText(Path.Combine(elsewhere, "kept.txt")));
      }
      finally
      {
         WorkingTreeTests.RemoveLink(link);
      }
   }



   [Fact]
   public async Task ALinkWhereTheRepositoryHasASubmodule_IsNotReplaced()
   {
      (string origin, string clone) = await CloneAnEmptyOriginAsync();
      CommitToOrigin(origin, (repo, tree) => tree
          .Add("readme.txt", BlobOf(repo, "theirs"), Mode.NonExecutableFile)
          .AddGitLink("sub", new ObjectId("1234567890123456789012345678901234567890")));
      string elsewhere = Directory.CreateDirectory(NewPath("elsewhere")).FullName; // empty: only the link itself is in the way
      string link = Path.Combine(clone, "sub");
      WorkingTreeTests.CreateDirectoryLink(link, elsewhere);
      try
      {
         await Assert.ThrowsAnyAsync<Exception>(() => _client.PullAsync(clone, Token, CancellationToken.None));

         Assert.NotNull(new DirectoryInfo(link).LinkTarget);
         Assert.False(File.Exists(Path.Combine(clone, "readme.txt")));
      }
      finally
      {
         WorkingTreeTests.RemoveLink(link);
      }
   }



   [Fact]
   public async Task FollowingOriginsDefaultBranch_ChangesNothing_BeforeItsTreeIsKnownToBeWritable()
   {
      if(!OperatingSystem.IsWindows())
      {
         return; // the paths are Windows' to refuse
      }
      (string origin, string clone) = await CloneAnEmptyOriginAsync();
      using(var bare = new Repository(origin))
      {
         bare.Refs.UpdateTarget("HEAD", "refs/heads/" + OtherBranch);
      }
      CommitToOrigin(origin, (repo, tree) => tree.Add("bad:name.txt", BlobOf(repo, "bad"), Mode.NonExecutableFile));
      string[] before = LocalConfiguration(clone);
      string headBefore;
      using(var repo = new Repository(clone))
      {
         headBefore = repo.Refs.Head.TargetIdentifier;
      }

      await Assert.ThrowsAsync<InvalidRepositoryPathsException>(() => _client.PullAsync(clone, Token, CancellationToken.None));

      Assert.Equal(before, LocalConfiguration(clone)); // no marker, no upstream for a branch that was not created
      using var after = new Repository(clone);
      Assert.Equal(headBefore, after.Refs.Head.TargetIdentifier); // and HEAD names what it named
   }



   [Fact]
   public async Task FollowingOriginsDefaultBranch_TheUpstreamIsConfigured_BeforeTheBranchExists()
   {
      (string origin, string clone) = await CloneAnEmptyOriginAsync();
      await SeedAsync(origin, OtherBranch, ("readme.txt", "from the other branch"));
      string heads = Directory.CreateDirectory(Path.Combine(clone, ".git", "refs", "heads")).FullName;
      string held = Path.Combine(heads, OtherBranch + ".lock"); // another git holds the lock: the branch cannot be created
      File.WriteAllText(held, "");

      await Assert.ThrowsAnyAsync<Exception>(() => _client.PullAsync(clone, Token, CancellationToken.None));

      using(var stopped = new Repository(clone))
      {
         Assert.True(stopped.Info.IsHeadUnborn);
         Assert.Equal("refs/heads/" + OtherBranch, stopped.Refs.Head.TargetIdentifier);
         Assert.Equal("refs/heads/" + OtherBranch, stopped.Config.Get<string>($"branch.{OtherBranch}.merge", ConfigurationLevel.Local)?.Value);
      }
      Assert.True(IsCheckoutPending(clone));

      File.Delete(held);
      await _client.PullAsync(clone, Token, CancellationToken.None);

      Assert.Equal("from the other branch", File.ReadAllText(Path.Combine(clone, "readme.txt")));
      Assert.False(IsCheckoutPending(clone));
   }



   // ---------------------------------------------------------------- review of #175, fourth round

   /// <summary>Cuts the first checkout of an empty clone off once its branch exists and before a file is written.</summary>
   private async Task CutOffBeforeTheFirstFileAsync(string clone)
   {
      using var cts = new CancellationTokenSource();
      _client.BeforeFirstCheckoutForTesting = cts.Cancel;
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _client.PullAsync(clone, Token, cts.Token));
      _client.BeforeFirstCheckoutForTesting = null;
      Assert.True(IsCheckoutPending(clone));
   }



   /// <summary>An origin with <c>a.txt</c>, <c>b.txt</c> and <c>zzz.txt</c> (the file a checkout that fails half-way stops at).</summary>
   private async Task<string> AnOriginOfThreeFilesAsync()
   {
      (string origin, _) = await CloneAnEmptyOriginAsync();
      CommitToOrigin(origin, (repo, tree) => tree
          .Add("a.txt", BlobOf(repo, "a"), Mode.NonExecutableFile)
          .Add("b.txt", BlobOf(repo, "b"), Mode.NonExecutableFile)
          .Add("zzz.txt", BlobOf(repo, "last"), Mode.NonExecutableFile));
      return origin;
   }



   [Theory]
   [InlineData("mine.txt", false)] // 'git add mine.txt'
   [InlineData("mywork/staged.txt", false)]
   [InlineData("mine.txt", true)] // 'git add -A'
   public async Task AFileSomebodyStaged_InAnUnfinishedTree_IsNotRemovedByTheCompletion(string path, bool everything)
   {
      string origin = await AnOriginOfThreeFilesAsync();
      string target = await ACloneWhoseCheckoutFailedHalfWayAsync(origin, "staged");
      File.Delete(Path.Combine(target, "zzz.txt")); // the tree is not whole
      string mine = Path.Combine(target, path.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
      File.WriteAllText(mine, "my notes");
      using(var repo = new Repository(target))
      {
         Commands.Stage(repo, everything ? "*" : path);
      }

      // A forced checkout removes what the index names and the commit does not have.
      InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.FetchAndPullAsync(origin, target, Token, CancellationToken.None));

      Assert.Contains($"'{path}'", refused.Message, StringComparison.Ordinal);
      Assert.Contains("git reset", refused.Message, StringComparison.Ordinal);
      Assert.Equal("my notes", File.ReadAllText(mine));
      Assert.True(IsCheckoutPending(target));

      // Out of the index it is a file beside the checkout, and the checkout is finished.
      using(var repo = new Repository(target))
      {
         repo.Index.Replace(repo.Head.Tip); // 'git reset'
         repo.Index.Write();
      }
      await _client.FetchAndPullAsync(origin, target, Token, CancellationToken.None);

      Assert.Equal("my notes", File.ReadAllText(mine));
      Assert.Equal("last", File.ReadAllText(Path.Combine(target, "zzz.txt")));
      Assert.False(IsCheckoutPending(target));
   }



   [Fact]
   public async Task ARenameStagedWithGit_InAFinishedCheckoutWhoseMarkerStayed_IsNotUndone()
   {
      string origin = await AnOriginOfThreeFilesAsync();
      string target = NewPath("renamed-here");
      await _client.CloneAsync(origin, target, Token, null, CancellationToken.None);
      string renamed = Path.Combine(target, "renamed.txt");
      using(var repo = new Repository(target))
      {
         repo.Config.Set("gclo.checkoutpending", true, ConfigurationLevel.Local);
         File.Move(Path.Combine(target, "b.txt"), renamed);
         Commands.Stage(repo, "b.txt");
         Commands.Stage(repo, "renamed.txt"); // 'git mv b.txt renamed.txt'
      }
      File.WriteAllText(renamed, "b, and an afternoon of work on it");

      await Assert.ThrowsAsync<InvalidOperationException>(() => _client.FetchAndPullAsync(origin, target, Token, CancellationToken.None));

      Assert.Equal("b, and an afternoon of work on it", File.ReadAllText(renamed));
      Assert.False(File.Exists(Path.Combine(target, "b.txt")));
   }



   [Fact]
   public async Task ALinkOnTheWayToASymbolicLinkOfTheRepository_IsRefused_AndNothingBehindItIsTouched()
   {
      (string origin, string clone) = await CloneAnEmptyOriginAsync();
      string elsewhere = Directory.CreateDirectory(NewPath("elsewhere")).FullName;
      string further = Directory.CreateDirectory(NewPath("further")).FullName;
      CommitToOrigin(origin, (repo, tree) => tree
          .Add("readme.txt", BlobOf(repo, "theirs"), Mode.NonExecutableFile)
          .Add("dir/alink", BlobOf(repo, further), Mode.SymbolicLink));
      await CutOffBeforeTheFirstFileAsync(clone);

      // The user's own link, outside the clone, which leads where the repository's
      // does; and the clone's "dir" leads to the folder that holds it.
      string outside = Path.Combine(elsewhere, "alink");
      WorkingTreeTests.CreateDirectoryLink(outside, further);
      string onTheWay = Path.Combine(clone, "dir");
      WorkingTreeTests.CreateDirectoryLink(onTheWay, elsewhere);
      try
      {
         InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
             () => _client.PullAsync(clone, Token, CancellationToken.None));

         Assert.Contains("is a link to another location", refused.Message, StringComparison.Ordinal);
         Assert.NotNull(new DirectoryInfo(outside).LinkTarget);
         Assert.NotNull(new DirectoryInfo(onTheWay).LinkTarget);
         Assert.True(IsCheckoutPending(clone));
      }
      finally
      {
         WorkingTreeTests.RemoveLink(onTheWay);
         WorkingTreeTests.RemoveLink(outside);
      }
   }



   [Fact]
   public async Task TheLinksACheckoutItselfWrote_AreItsOwn_WhateverThisMachineLetsItWrite()
   {
      // With the right to create symbolic links a checkout writes links (the target
      // with this system's separators); without it, files that name their targets.
      (string origin, _) = await CloneAnEmptyOriginAsync();
      CommitToOrigin(origin, (repo, tree) => tree
          .Add("alink", BlobOf(repo, "readme.txt"), Mode.SymbolicLink)
          .Add("docs/up", BlobOf(repo, "../readme.txt"), Mode.SymbolicLink)
          .Add("readme.txt", BlobOf(repo, "theirs"), Mode.NonExecutableFile)
          .Add("zzz.txt", BlobOf(repo, "last"), Mode.NonExecutableFile));
      string target = await ACloneWhoseCheckoutFailedHalfWayAsync(origin, "links");

      await _client.FetchAndPullAsync(origin, target, Token, CancellationToken.None);

      Assert.Equal("last", File.ReadAllText(Path.Combine(target, "zzz.txt")));
      Assert.False(IsCheckoutPending(target));
   }



   [Fact]
   public async Task AFinishedCheckoutWhoseMarkerStayed_IsWhole_AlsoWhereItHoldsALinkToAFolder()
   {
      (string origin, _) = await CloneAnEmptyOriginAsync();
      CommitToOrigin(origin, (repo, tree) => tree
          .Add("alink", BlobOf(repo, "docs"), Mode.SymbolicLink)
          .Add("docs/guide.txt", BlobOf(repo, "guide"), Mode.NonExecutableFile)
          .Add("other.txt", BlobOf(repo, "other"), Mode.NonExecutableFile));
      string target = NewPath("finished-with-a-link");
      await _client.CloneAsync(origin, target, Token, null, CancellationToken.None);
      string link = Path.Combine(target, "alink");
      if(File.GetAttributes(link).HasFlag(FileAttributes.Directory))
      {
         Directory.Delete(link);
      }
      else
      {
         File.Delete(link);
      }
      WorkingTreeTests.CreateDirectoryLink(link, Path.Combine(target, "docs")); // a link to a folder: no file is there
      using(var repo = new Repository(target))
      {
         repo.Config.Set("gclo.checkoutpending", true, ConfigurationLevel.Local);
      }
      File.WriteAllText(Path.Combine(target, "other.txt"), "edited here"); // a completion would refuse this, or undo it
      try
      {
         await _client.FetchAndPullAsync(origin, target, Token, CancellationToken.None);

         Assert.Equal("edited here", File.ReadAllText(Path.Combine(target, "other.txt")));
         Assert.False(IsCheckoutPending(target));
      }
      finally
      {
         WorkingTreeTests.RemoveLink(link);
      }
   }



   [Fact]
   public async Task AFileWrittenBeforeTheRulesThatChangeIt_IsStillTheCheckoutsOwn()
   {
      // "-early.txt" is written before ".gitattributes": as it is stored, not as the
      // 'ident' rule in that file makes it.
      (string origin, string clone) = await CloneAnEmptyOriginAsync();
      CommitToOrigin(origin, (repo, tree) => tree
          .Add("-early.txt", BlobOf(repo, "$Id$" + LF + "hello" + LF), Mode.NonExecutableFile)
          .Add(".gitattributes", BlobOf(repo, "*.txt ident" + LF), Mode.NonExecutableFile));
      await CutOffBeforeTheFirstFileAsync(clone);
      File.WriteAllText(Path.Combine(clone, "-early.txt"), "$Id$" + LF + "hello" + LF);
      File.WriteAllText(Path.Combine(clone, ".gitattributes"), "*.txt ident" + LF);

      await _client.PullAsync(clone, Token, CancellationToken.None);

      Assert.False(IsCheckoutPending(clone));
   }



   [Fact]
   public async Task ARecoveryAfterTheBranchMoved_KnowsTheFilesOfTheCommitItLeft_AndTakesTheListOff()
   {
      if(!OperatingSystem.IsWindows())
      {
         return; // the paths are Windows' to refuse
      }
      (string origin, string clone) = await ACloneWhoseFirstCheckoutWasCutOffAsync(("a.txt", "one"));
      File.WriteAllText(Path.Combine(clone, "a.txt"), "one"); // what the cut-off checkout had written
      CommitToOrigin(origin, (repo, tree) => tree
          .Add("a.txt", BlobOf(repo, "two"), Mode.NonExecutableFile)
          .Add("bad:name.txt", BlobOf(repo, "bad"), Mode.NonExecutableFile));
      await Assert.ThrowsAsync<InvalidRepositoryPathsException>(() => _client.PullAsync(clone, Token, CancellationToken.None));
      Assert.NotNull(WrittenBefore(clone));

      await _client.ApplyRecoveryAsync(
          clone,
          new PathRecovery(
              new Dictionary<string, string>(StringComparer.Ordinal) { ["bad:name.txt"] = "bad_name.txt" },
              new HashSet<string>(StringComparer.Ordinal)),
          CancellationToken.None);

      Assert.Equal("two", File.ReadAllText(Path.Combine(clone, "a.txt")));
      Assert.Equal("bad", File.ReadAllText(Path.Combine(clone, "bad_name.txt")));
      Assert.False(IsCheckoutPending(clone));
      Assert.Null(WrittenBefore(clone));
   }



   [Fact]
   public async Task AListOfEarlierCommits_ThatHoldsWhatIsNotACommitHere_IsReadWithoutFailing()
   {
      (_, string clone) = await ACloneWhoseFirstCheckoutWasCutOffAsync(("a.txt", "one"));
      File.WriteAllText(Path.Combine(clone, "a.txt"), "on"); // the file the checkout was writing
      using(var repo = new Repository(clone))
      {
         string blob = repo.Head.Tip["a.txt"].Target.Sha;
         string tree = repo.Head.Tip.Tree.Sha;
         repo.Config.Set(
             "gclo.checkoutwrittenbefore",
             $"garbage,0123456789012345678901234567890123456789,{blob},{tree}, ,abc,{new string('f', 40)},",
             ConfigurationLevel.Local);
      }

      await _client.PullAsync(clone, Token, CancellationToken.None);

      Assert.Equal("one", File.ReadAllText(Path.Combine(clone, "a.txt")));
      Assert.False(IsCheckoutPending(clone));
      Assert.Null(WrittenBefore(clone));
   }



   [Fact]
   public async Task TheCommitsAnUnfinishedCheckoutLeft_AreRecordedOnce_AndTheLatestSixteenAreKept()
   {
      if(!OperatingSystem.IsWindows())
      {
         return; // the paths are Windows' to refuse
      }
      (string origin, string clone) = await ACloneWhoseFirstCheckoutWasCutOffAsync(("a.txt", "one"));
      var left = new List<string> { HeadSha(clone) };
      for(int round = 0; round < 17; round++)
      {
         string content = new('x', round + 1);
         string added = CommitToOrigin(origin, (repo, tree) => tree.Add("bad:name.txt", BlobOf(repo, content), Mode.NonExecutableFile));

         // The first sync moves the branch off the commit before; the second, with
         // nothing new at origin, finds the branch on a commit Windows cannot write
         // and records that one; a third would record nothing new.
         await Assert.ThrowsAsync<InvalidRepositoryPathsException>(() => _client.PullAsync(clone, Token, CancellationToken.None));
         await Assert.ThrowsAsync<InvalidRepositoryPathsException>(() => _client.PullAsync(clone, Token, CancellationToken.None));
         await Assert.ThrowsAsync<InvalidRepositoryPathsException>(() => _client.PullAsync(clone, Token, CancellationToken.None));
         left.Add(added);
      }

      Assert.Equal(18, left.Count);
      Assert.Equal(string.Join(',', left.TakeLast(16)), WrittenBefore(clone));
   }
}
