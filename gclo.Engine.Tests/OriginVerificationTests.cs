/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using LibGit2Sharp;
using static gclo.Engine.Tests.GitTestHelpers;


namespace gclo.Engine.Tests;


/// <summary>
/// A pull is given the clone URL the repository was listed with, and only pulls a
/// folder that is a clone of that repository (#139). Real repositories on the local
/// file system, like <see cref="LibGit2GitClientTests"/>.
/// </summary>
public sealed class OriginVerificationTests : IDisposable
{
   private const string Token = "test-token";

   private readonly string _root =
       Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

   private readonly LibGit2GitClient _client = new();



   public OriginVerificationTests()
   {
      Directory.CreateDirectory(_root);
   }



   public void Dispose() => TryDeleteDirectory(_root);



   [Fact]
   public async Task Pull_FolderIsACloneOfAnotherRepository_FailsWithoutFetching()
   {
      string listed = CreateSourceRepo("listed");
      string unrelated = CreateSourceRepo("unrelated");
      string target = Path.Combine(_root, "clone");
      await _client.CloneAsync(unrelated, target, Token, null, CancellationToken.None);
      string tipBefore = HeadSha(target);
      string trackingBefore = TrackingTip(target);
      // Something to fetch from the folder's own origin, had it been contacted.
      CommitFile(unrelated, "more.txt", "more", "second commit");

      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.FetchAndPullAsync(listed, target, Token, CancellationToken.None));

      Assert.Contains("clone of a different repository", ex.Message);
      Assert.Contains(unrelated, ex.Message);
      Assert.Contains(listed, ex.Message);
      // A renamed or transferred repository looks exactly like this; the message says
      // how to point the folder at the new URL.
      Assert.Contains($"git -C \"{target}\" remote set-url origin {listed}", ex.Message);
      Assert.Equal(tipBefore, HeadSha(target));
      Assert.Equal(trackingBefore, TrackingTip(target));
   }



   [Fact]
   public async Task Pull_OriginSpelledDifferently_IsTheSameRepository_AndIsPulledFromTheListedUrl()
   {
      string source = CreateSourceRepo("source");
      string target = Path.Combine(_root, "clone");
      await _client.CloneAsync(source, target, Token, null, CancellationToken.None);
      using(var repo = new Repository(target))
      {
         // The same folder, written as a file URI: what counts is which repository
         // the origin names, not how the URL is spelled.
         repo.Network.Remotes.Update("origin", r => r.Url = new Uri(source).AbsoluteUri);
      }
      string newSha = CommitFile(source, "update.txt", "new content", "second commit");

      await _client.FetchAndPullAsync(source, target, Token, CancellationToken.None);

      Assert.Equal(newSha, HeadSha(target));
      Assert.Equal(newSha, TrackingTip(target));
      Assert.Equal("new content", File.ReadAllText(Path.Combine(target, "update.txt")));
      using(var repo = new Repository(target))
      {
         // Origin keeps the spelling the user gave it, and the remote used for the
         // fetch is gone.
         Assert.Equal(new Uri(source).AbsoluteUri, repo.Network.Remotes["origin"].Url);
         Assert.Equal("origin", Assert.Single(repo.Network.Remotes).Name);
      }
      // What was contacted is the listed URL, not origin's spelling of it: FETCH_HEAD
      // records the URL of the remote a fetch used.
      string fetchHead = File.ReadAllText(Path.Combine(target, ".git", "FETCH_HEAD"));
      Assert.Contains(source.Replace('\\', '/'), fetchHead.Replace('\\', '/'));
      Assert.DoesNotContain("file://", fetchHead);
   }



   [Fact]
   public async Task Pull_OriginSpelledDifferently_StillFollowsTags_AndPrunesDeletedBranches()
   {
      string source = CreateSourceRepo("source");
      using(var repo = new Repository(source))
      {
         repo.CreateBranch("topic");
      }
      string target = Path.Combine(_root, "clone");
      await _client.CloneAsync(source, target, Token, null, CancellationToken.None);
      using(var repo = new Repository(target))
      {
         repo.Network.Remotes.Update("origin", r => r.Url = new Uri(source).AbsoluteUri);
      }
      string newSha = CommitFile(source, "update.txt", "new content", "second commit");
      using(var repo = new Repository(source))
      {
         repo.ApplyTag("v1.0.0");
         repo.Branches.Remove("topic");
      }

      await _client.FetchAndPullAsync(source, target, Token, CancellationToken.None);

      using(var repo = new Repository(target))
      {
         Assert.Equal(newSha, repo.Head.Tip.Sha);
         Assert.NotNull(repo.Tags["v1.0.0"]);
         Assert.Null(repo.Branches["origin/topic"]);
         // Removing the fetch's remote must not take origin's tracking refs with it.
         Assert.NotNull(repo.Head.TrackedBranch?.Tip);
      }
   }



   [Fact]
   public async Task Pull_OriginSpelledDifferently_ARemoteLeftByAKilledRun_IsReplaced_NotFetched()
   {
      string source = CreateSourceRepo("source");
      string target = Path.Combine(_root, "clone");
      await _client.CloneAsync(source, target, Token, null, CancellationToken.None);
      using(var repo = new Repository(target))
      {
         repo.Network.Remotes.Update("origin", r => r.Url = new Uri(source).AbsoluteUri);
         // What a process killed mid-fetch leaves behind, pointing somewhere stale.
         repo.Network.Remotes.Add("gclo-sync", Path.Combine(_root, "gone"));
         repo.Config.Set("remote.gclo-sync.gclo-temporary", true, ConfigurationLevel.Local);
      }
      string newSha = CommitFile(source, "update.txt", "new content", "second commit");

      await _client.FetchAndPullAsync(source, target, Token, CancellationToken.None);

      Assert.Equal(newSha, HeadSha(target));
      using(var repo = new Repository(target))
      {
         Assert.Equal("origin", Assert.Single(repo.Network.Remotes).Name);
      }
   }



   [Fact]
   public async Task Pull_StillFollowsTags_AndPrunesDeletedBranches()
   {
      // The fetch names the listed URL rather than the remote "origin"; it must keep
      // doing what a fetch of origin does: bring tags on fetched commits and drop
      // tracking refs of branches origin no longer has.
      string source = CreateSourceRepo("source");
      using(var repo = new Repository(source))
      {
         repo.CreateBranch("topic");
      }
      string target = Path.Combine(_root, "clone");
      await _client.CloneAsync(source, target, Token, null, CancellationToken.None);
      using(var repo = new Repository(target))
      {
         Assert.NotNull(repo.Branches["origin/topic"]);
      }
      CommitFile(source, "update.txt", "new content", "second commit");
      using(var repo = new Repository(source))
      {
         repo.ApplyTag("v1.0.0");
         repo.Branches.Remove("topic");
      }

      await _client.PullAsync(target, Token, CancellationToken.None);

      using(var repo = new Repository(target))
      {
         Assert.NotNull(repo.Tags["v1.0.0"]);
         Assert.Null(repo.Branches["origin/topic"]);
      }
   }



   [Fact]
   public async Task Pull_MismatchMessage_NeverShowsCredentialsStoredInTheOrigin()
   {
      string listed = CreateSourceRepo("listed");
      string target = Path.Combine(_root, "clone");
      await _client.CloneAsync(listed, target, Token, null, CancellationToken.None);
      using(var repo = new Repository(target))
      {
         repo.Network.Remotes.Update("origin", r => r.Url = "https://someone:hunter2@example.test/acme/other.git");
      }

      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.FetchAndPullAsync(listed, target, Token, CancellationToken.None));

      Assert.Contains("https://example.test/acme/other.git", ex.Message);
      Assert.DoesNotContain("hunter2", ex.Message);
      Assert.DoesNotContain("someone", ex.Message);
   }



   [Fact]
   public async Task Pull_ARemoteLeftByAKilledRun_IsAlsoRemoved_WhenOriginIsTheListedUrl()
   {
      string source = CreateSourceRepo("source");
      string target = Path.Combine(_root, "clone");
      await _client.CloneAsync(source, target, Token, null, CancellationToken.None);
      using(var repo = new Repository(target))
      {
         repo.Network.Remotes.Add("gclo-sync", Path.Combine(_root, "gone"));
         repo.Config.Set("remote.gclo-sync.gclo-temporary", true, ConfigurationLevel.Local);
      }

      await _client.PullAsync(target, Token, CancellationToken.None);

      using(var repo2 = new Repository(target))
      {
         Assert.Equal("origin", Assert.Single(repo2.Network.Remotes).Name);
      }
   }



   [Fact]
   public async Task Pull_TheUsersOwnRemoteNamedGcloSync_IsNeverRemoved()
   {
      string source = CreateSourceRepo("source");
      string elsewhere = CreateSourceRepo("elsewhere");
      string target = Path.Combine(_root, "clone");
      await _client.CloneAsync(source, target, Token, null, CancellationToken.None);
      using(var repo = new Repository(target))
      {
         repo.Network.Remotes.Add("gclo-sync", elsewhere); // theirs: no gclo marker
      }

      // Origin is the listed URL: the name is not needed, and their remote stays.
      await _client.PullAsync(target, Token, CancellationToken.None);
      using(var repo = new Repository(target))
      {
         Assert.Equal(elsewhere, repo.Network.Remotes["gclo-sync"].Url);
         repo.Network.Remotes.Update("origin", r => r.Url = new Uri(source).AbsoluteUri);
      }

      // Origin is spelled differently: the name is needed, so the pull says so instead
      // of taking it over.
      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.FetchAndPullAsync(source, target, Token, CancellationToken.None));

      Assert.Contains("remote of its own named 'gclo-sync'", ex.Message);
      using(var repo = new Repository(target))
      {
         Assert.Equal(elsewhere, repo.Network.Remotes["gclo-sync"].Url);
      }
   }



   [Fact]
   public async Task Pull_FolderConfigurationRewritesUrls_IsRefused_BeforeAnythingIsFetched()
   {
      string source = CreateSourceRepo("source");
      string other = CreateSourceRepo("other");
      string target = Path.Combine(_root, "clone");
      await _client.CloneAsync(source, target, Token, null, CancellationToken.None);
      string trackingBefore = TrackingTip(target);
      using(var repo = new Repository(target))
      {
         // url.<other>.insteadOf = <source>: every fetch of the listed URL would
         // really contact the other repository.
         repo.Config.Set($"url.{other}.insteadof", source, ConfigurationLevel.Local);
      }
      CommitFile(source, "update.txt", "new content", "second commit");
      CommitFile(other, "planted.txt", "not what was listed", "planted commit");

      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.FetchAndPullAsync(source, target, Token, CancellationToken.None));

      Assert.Contains("url.<base>.insteadOf", ex.Message);
      Assert.Equal(trackingBefore, TrackingTip(target));
      Assert.False(File.Exists(Path.Combine(target, "planted.txt")));
   }



   [Fact]
   public async Task Pull_FolderConfigurationNamesAnSshProgram_IsRefused_WhenTheFetchIsNotHttps()
   {
      // The fixtures are local paths, which stand in here for "not HTTPS": the case
      // where the user's global configuration sends the fetch over SSH.
      string source = CreateSourceRepo("source");
      string target = Path.Combine(_root, "clone");
      await _client.CloneAsync(source, target, Token, null, CancellationToken.None);
      string trackingBefore = TrackingTip(target);
      using(var repo = new Repository(target))
      {
         repo.Config.Set("core.sshCommand", "calc.exe", ConfigurationLevel.Local);
      }
      CommitFile(source, "update.txt", "new content", "second commit");

      InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => _client.PullAsync(target, Token, CancellationToken.None));

      Assert.Contains("core.sshCommand", ex.Message);
      Assert.Equal(trackingBefore, TrackingTip(target));
   }



   // ---------------------------------------------------------------- helpers

   private string CreateSourceRepo(string name)
   {
      string path = Path.Combine(_root, name);
      Repository.Init(path);
      CommitFile(path, "readme.txt", "hello from " + name, "initial commit");
      return path;
   }



   private static string CommitFile(string workdir, string fileName, string content, string message)
   {
      using var repo = new Repository(workdir);
      File.WriteAllText(Path.Combine(workdir, fileName), content);
      Commands.Stage(repo, fileName);
      Signature signature = MakeSignature();
      return repo.Commit(message, signature, signature).Sha;
   }



   /// <summary>Tip of the remote-tracking ref of the clone's current branch.</summary>
   private static string TrackingTip(string workdir)
   {
      using var repo = new Repository(workdir);
      return repo.Head.TrackedBranch.Tip.Sha;
   }
}
