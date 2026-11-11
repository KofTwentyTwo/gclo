/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.ViewModels;
using static gclo.Engine.Tests.GitTestHelpers;


namespace gclo.Engine.Tests;


/// <summary>
/// Tests for <see cref="SyncAllCoordinator"/> over fake-backed, account-seeded
/// <see cref="WorkspaceViewModel"/>s: strict sequential order, load-before-sync,
/// skip accounting, and queue-level cancellation that never touches the in-flight sync.
/// </summary>
public sealed class SyncAllCoordinatorTests : IDisposable
{
   private readonly string _root =
       Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

   private readonly InMemoryVault _vault = new();

   private readonly OrderedLister _lister = new();

   private readonly FakeOrganizationLister _orgs = new();

   private readonly List<WorkspaceViewModel> _workspaces = new();

   private readonly SyncAllCoordinator _coordinator = new(new NullActivityLog());



   public void Dispose()
   {
      foreach(WorkspaceViewModel workspace in _workspaces)
      {
         workspace.Dispose();
      }
      TryDeleteDirectory(_root);
   }



   /// <summary>
   /// Synchronous progress: the handler runs inline on the reporting thread, one
   /// report at a time. The view model's handler belongs to the UI thread and is
   /// never entered twice at once there; several workers reporting at the same
   /// moment must not enter it together here either.
   /// </summary>
   private sealed class SyncProgress(Action<RepoProgress> handler) : IProgress<RepoProgress>
   {
      private readonly Lock _oneAtATime = new();

      public void Report(RepoProgress value)
      {
         lock(_oneAtATime)
         {
            handler(value);
         }
      }
   }



   /// <summary>
   /// One lister shared by every workspace, so the recorded organization order proves
   /// globally which account loaded when. Behavior is scripted per organization.
   /// </summary>
   private sealed class OrderedLister : IRepositoryLister
   {
      private readonly List<string> _organizations = new();

      private readonly Lock _gate = new();

      /// <summary>Repositories (or exception) per organization. Default: one repo named '&lt;org&gt;-repo'.</summary>
      public Func<string, IReadOnlyList<RepoDescriptor>> Handler { get; set; }
          = org => new[] { Repo(org + "-repo") };



      /// <summary>Organizations listed so far, in call order.</summary>
      public IReadOnlyList<string> Organizations
      {
         get
         {
            lock(_gate)
            {
               return _organizations.ToArray();
            }
         }
      }



      public Task<IReadOnlyList<RepoDescriptor>> ListOrganizationRepositoriesAsync(
          string organization, string token, CancellationToken cancellationToken = default)
      {
         lock(_gate)
         {
            _organizations.Add(organization);
         }
         try
         {
            return Task.FromResult(Handler(organization));
         }
         catch(Exception ex)
         {
            return Task.FromException<IReadOnlyList<RepoDescriptor>>(ex);
         }
      }
   }



   /// <summary>
   /// An account-backed workspace syncing organization 'org-&lt;name&gt;' into a folder
   /// under the test root. The seeded vault token is deliberately shorter than a
   /// plausible PAT so the constructor's debounced org lookup never fires.
   /// </summary>
   private WorkspaceViewModel CreateWorkspace(string name, FakeGitClient? git = null)
   {
      var account = new Account
      {
         Id = Guid.NewGuid(),
         Name = name,
         Organization = "org-" + name,
         TargetRoot = Path.Combine(_root, name),
      };
      _vault.Store(account.Id, "tok-" + name);

      var workspace = new WorkspaceViewModel(
          _lister,
          git ?? new FakeGitClient(),
          _orgs,
          handler => new SyncProgress(handler),
          TimeSpan.FromMilliseconds(1),
          new NullActivityLog(),
          account,
          _vault);
      _workspaces.Add(workspace);
      return workspace;
   }



   // ---------------------------------------------------------------- default token (#102)

   /// <summary>A default-token account workspace; the default token is seeded only when asked.</summary>
   private WorkspaceViewModel CreateDefaultTokenWorkspace(string name, bool withDefaultToken)
   {
      var account = new Account
      {
         Id = Guid.NewGuid(),
         Name = name,
         Organization = "org-" + name,
         TargetRoot = Path.Combine(_root, name),
         TokenSource = TokenSource.Default,
      };
      if(withDefaultToken)
      {
         _vault.Store(AppSettings.DefaultTokenVaultId, "tok-default");
      }
      var workspace = new WorkspaceViewModel(
          _lister,
          new FakeGitClient(),
          _orgs,
          handler => new SyncProgress(handler),
          TimeSpan.FromMilliseconds(1),
          new NullActivityLog(),
          account,
          _vault);
      _workspaces.Add(workspace);
      return workspace;
   }



   [Fact]
   public async Task RunAsync_DefaultTokenAccount_WithoutADefaultToken_IsSkipped_NotRun()
   {
      WorkspaceViewModel shared = CreateDefaultTokenWorkspace("shared", withDefaultToken: false);
      Assert.True(shared.UsesDefaultToken);
      Assert.Equal("", shared.Token);
      var states = new List<SyncAllAccountState>();
      _coordinator.AccountStateChanged = (_, state) => states.Add(state);

      SyncAllResult result = await _coordinator.RunAsync([shared], CancellationToken.None);

      Assert.Equal(0, result.Ran);
      Assert.Equal(1, result.Skipped);
      Assert.Equal([SyncAllAccountState.Queued, SyncAllAccountState.Skipped], states);
      Assert.Empty(_lister.Organizations); // never asked GitHub: skipped before loading
   }



   [Fact]
   public async Task RunAsync_DefaultTokenAccount_WithADefaultToken_Runs()
   {
      WorkspaceViewModel shared = CreateDefaultTokenWorkspace("shared", withDefaultToken: true);
      Assert.Equal("tok-default", shared.Token);

      SyncAllResult result = await _coordinator.RunAsync([shared], CancellationToken.None);

      Assert.Equal(1, result.Ran);
      Assert.Equal(0, result.Skipped);
   }

   // ---------------------------------------------------------------- ordering

   [Fact]
   public async Task RunAsync_RunsAccountsStrictlySequentially_InListOrder()
   {
      var gitAlpha = new FakeGitClient();
      var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      gitAlpha.CloneHandler = (_, _, _, _, _) =>
      {
         entered.TrySetResult();
         return gate.Task;
      };
      WorkspaceViewModel alpha = CreateWorkspace("alpha", gitAlpha);
      WorkspaceViewModel beta = CreateWorkspace("beta");

      Task<SyncAllResult> run = _coordinator.RunAsync(new[] { alpha, beta }, CancellationToken.None);

      await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); // alpha's sync is in flight...
      Assert.Equal(new[] { "org-alpha" }, _lister.Organizations); // ...and beta has not started

      gate.SetResult();
      SyncAllResult result = await run.WaitAsync(TimeSpan.FromSeconds(30));

      Assert.Equal(new SyncAllResult(Ran: 2, Skipped: 0, WasCanceled: false), result);
      Assert.Equal(new[] { "org-alpha", "org-beta" }, _lister.Organizations);
   }



   // ---------------------------------------------------------------- load-before-sync

   [Fact]
   public async Task RunAsync_LoadsAnUnloadedWorkspace_ThenSyncsIt()
   {
      var git = new FakeGitClient();
      WorkspaceViewModel workspace = CreateWorkspace("alpha", git);
      Assert.False(workspace.HasLoadedRepos);

      SyncAllResult result = await _coordinator.RunAsync(new[] { workspace }, CancellationToken.None);

      Assert.Equal(new SyncAllResult(Ran: 1, Skipped: 0, WasCanceled: false), result);
      Assert.True(workspace.HasLoadedRepos);
      Assert.Equal(new[] { "org-alpha-repo" }, git.ClonedRepoNames);
   }



   [Fact]
   public async Task RunAsync_DoesNotReloadAnAlreadyLoadedWorkspace()
   {
      var git = new FakeGitClient();
      WorkspaceViewModel workspace = CreateWorkspace("alpha", git);
      await workspace.LoadReposCommand.ExecuteAsync(null);
      Assert.Equal(new[] { "org-alpha" }, _lister.Organizations);

      SyncAllResult result = await _coordinator.RunAsync(new[] { workspace }, CancellationToken.None);

      Assert.Equal(new SyncAllResult(Ran: 1, Skipped: 0, WasCanceled: false), result);
      Assert.Equal(new[] { "org-alpha" }, _lister.Organizations); // still exactly one listing
      Assert.Equal(new[] { "org-alpha-repo" }, git.ClonedRepoNames);
   }



   // ---------------------------------------------------------------- skipping

   [Fact]
   public async Task RunAsync_SkipsBusyWorkspaces_AndStillRunsTheRest()
   {
      WorkspaceViewModel running = CreateWorkspace("alpha");
      running.IsRunning = true;
      WorkspaceViewModel loading = CreateWorkspace("beta");
      loading.IsLoadingRepos = true;
      WorkspaceViewModel resolving = CreateWorkspace("gamma");
      resolving.IsResolvingPaths = true;
      var git = new FakeGitClient();
      WorkspaceViewModel idle = CreateWorkspace("delta", git);

      SyncAllResult result = await _coordinator.RunAsync(
          new[] { running, loading, resolving, idle }, CancellationToken.None);

      Assert.Equal(new SyncAllResult(Ran: 1, Skipped: 3, WasCanceled: false), result);
      Assert.Equal(new[] { "org-delta" }, _lister.Organizations); // busy ones were never even loaded
      Assert.Equal(new[] { "org-delta-repo" }, git.ClonedRepoNames);
   }



   [Fact]
   public async Task RunAsync_SkipsAWorkspaceWhoseLoadFails_AndMovesOn()
   {
      _lister.Handler = org => string.Equals(org, "org-alpha"
, StringComparison.Ordinal) ? throw new InvalidOperationException("org not found")
          : new[] { Repo(org + "-repo") };
      WorkspaceViewModel alpha = CreateWorkspace("alpha");
      var git = new FakeGitClient();
      WorkspaceViewModel beta = CreateWorkspace("beta", git);

      SyncAllResult result = await _coordinator.RunAsync(new[] { alpha, beta }, CancellationToken.None);

      Assert.Equal(new SyncAllResult(Ran: 1, Skipped: 1, WasCanceled: false), result);
      Assert.False(alpha.HasLoadedRepos);
      Assert.Equal(new[] { "org-beta-repo" }, git.ClonedRepoNames);
   }



   [Fact]
   public async Task RunAsync_SkipsALoadedWorkspaceWhoseSyncCannotRun()
   {
      _lister.Handler = _ => Array.Empty<RepoDescriptor>();
      WorkspaceViewModel workspace = CreateWorkspace("alpha");

      SyncAllResult result = await _coordinator.RunAsync(new[] { workspace }, CancellationToken.None);

      Assert.Equal(new SyncAllResult(Ran: 0, Skipped: 1, WasCanceled: false), result);
      Assert.True(workspace.HasLoadedRepos); // it loaded fine; there was just nothing to sync
   }



   // ---------------------------------------------------------------- cancellation

   [Fact]
   public async Task RunAsync_CancellationStopsTheQueue_ButLetsTheInFlightSyncFinish()
   {
      var gitAlpha = new FakeGitClient();
      var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      gitAlpha.CloneHandler = (_, _, _, _, _) =>
      {
         entered.TrySetResult();
         return gate.Task;
      };
      WorkspaceViewModel alpha = CreateWorkspace("alpha", gitAlpha);
      var gitBeta = new FakeGitClient();
      WorkspaceViewModel beta = CreateWorkspace("beta", gitBeta);
      using var cts = new CancellationTokenSource();

      Task<SyncAllResult> run = _coordinator.RunAsync(new[] { alpha, beta }, cts.Token);
      await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
      cts.Cancel(); // requested while alpha's sync is in flight...
      gate.SetResult(); // ...which must still run to completion
      SyncAllResult result = await run.WaitAsync(TimeSpan.FromSeconds(30));

      Assert.Equal(new SyncAllResult(Ran: 1, Skipped: 0, WasCanceled: true), result);
      Assert.Equal(SyncStatus.Done, alpha.Repos[0].Status); // finished, not canceled
      Assert.False(alpha.IsRunning);
      Assert.False(beta.HasLoadedRepos); // the second account never started
      Assert.Empty(gitBeta.CloneCalls);
      Assert.Equal(new[] { "org-alpha" }, _lister.Organizations);
   }



   [Fact]
   public async Task RunAsync_TokenAlreadyCanceled_StartsNothing()
   {
      var git = new FakeGitClient();
      WorkspaceViewModel workspace = CreateWorkspace("alpha", git);
      using var cts = new CancellationTokenSource();
      cts.Cancel();

      SyncAllResult result = await _coordinator.RunAsync(new[] { workspace }, cts.Token);

      Assert.Equal(new SyncAllResult(Ran: 0, Skipped: 0, WasCanceled: true), result);
      Assert.False(workspace.HasLoadedRepos);
      Assert.Empty(git.CloneCalls);
   }



   // ---------------------------------------------------------------- queue states

   private List<(Guid Id, SyncAllAccountState State)> RecordStates()
   {
      var states = new List<(Guid, SyncAllAccountState)>();
      _coordinator.AccountStateChanged = (id, state) => states.Add((id, state));
      return states;
   }



   [Fact]
   public async Task RunAsync_AnnouncesQueuedForAll_ThenRunningAndTerminalPerAccount()
   {
      WorkspaceViewModel alpha = CreateWorkspace("alpha");
      WorkspaceViewModel beta = CreateWorkspace("beta");
      List<(Guid Id, SyncAllAccountState State)> states = RecordStates();

      await _coordinator.RunAsync(new[] { alpha, beta }, CancellationToken.None);

      Assert.Equal(
          new[]
          {
                (alpha.AccountId!.Value, SyncAllAccountState.Queued),
                (beta.AccountId!.Value, SyncAllAccountState.Queued),
                (alpha.AccountId!.Value, SyncAllAccountState.Running),
                (alpha.AccountId!.Value, SyncAllAccountState.Succeeded),
                (beta.AccountId!.Value, SyncAllAccountState.Running),
                (beta.AccountId!.Value, SyncAllAccountState.Succeeded),
          },
          states);
   }



   [Fact]
   public async Task RunAsync_AccountWithFailedRepos_AnnouncesFailed()
   {
      var git = new FakeGitClient
      {
         CloneHandler = (_, _, _, _, _) =>
             Task.FromException(new InvalidOperationException("boom")),
      };
      WorkspaceViewModel workspace = CreateWorkspace("alpha", git);
      List<(Guid Id, SyncAllAccountState State)> states = RecordStates();

      await _coordinator.RunAsync(new[] { workspace }, CancellationToken.None);

      Assert.Equal(SyncAllAccountState.Failed, states[^1].State);
   }



   [Fact]
   public async Task RunAsync_BusyAccount_AnnouncesSkippedWithoutRunning()
   {
      WorkspaceViewModel workspace = CreateWorkspace("alpha");
      workspace.IsResolvingPaths = true; // busy: must be skipped
      List<(Guid Id, SyncAllAccountState State)> states = RecordStates();

      await _coordinator.RunAsync(new[] { workspace }, CancellationToken.None);

      Assert.Equal(
          new[]
          {
                (workspace.AccountId!.Value, SyncAllAccountState.Queued),
                (workspace.AccountId!.Value, SyncAllAccountState.Skipped),
          },
          states);
   }



   [Fact]
   public async Task RunAsync_CanceledQueue_AnnouncesSkippedForUnstartedAccounts()
   {
      WorkspaceViewModel alpha = CreateWorkspace("alpha");
      WorkspaceViewModel beta = CreateWorkspace("beta");
      using var cts = new CancellationTokenSource();
      cts.Cancel();
      List<(Guid Id, SyncAllAccountState State)> states = RecordStates();

      await _coordinator.RunAsync(new[] { alpha, beta }, cts.Token);

      Assert.Equal(
          new[]
          {
                (alpha.AccountId!.Value, SyncAllAccountState.Queued),
                (beta.AccountId!.Value, SyncAllAccountState.Queued),
                (alpha.AccountId!.Value, SyncAllAccountState.Skipped),
                (beta.AccountId!.Value, SyncAllAccountState.Skipped),
          },
          states);
   }



   // ---------------------------------------------------------------- a token saved after the workspace opened (#157)

   [Fact]
   public async Task RunAsync_DefaultTokenAccount_WhoseTokenWasSavedAfterItOpened_Runs()
   {
      WorkspaceViewModel shared = CreateDefaultTokenWorkspace("shared", withDefaultToken: false);
      Assert.Equal("", shared.Token);
      _vault.Store(AppSettings.DefaultTokenVaultId, "tok-saved-later"); // saved in Settings meanwhile

      SyncAllResult result = await _coordinator.RunAsync([shared], CancellationToken.None);

      Assert.Equal(1, result.Ran);
      Assert.Equal(0, result.Skipped);
      Assert.Equal("tok-saved-later", shared.Token);
   }



   [Fact]
   public async Task RunAsync_DefaultTokenAccount_WhoseTokenWasRemovedAfterItOpened_IsSkipped()
   {
      WorkspaceViewModel shared = CreateDefaultTokenWorkspace("shared", withDefaultToken: true);
      _vault.Delete(AppSettings.DefaultTokenVaultId); // removed in Settings meanwhile

      SyncAllResult result = await _coordinator.RunAsync([shared], CancellationToken.None);

      Assert.Equal(0, result.Ran);
      Assert.Equal(1, result.Skipped);
      Assert.Empty(_lister.Organizations); // GitHub was never asked with the old token
   }



   // ---------------------------------------------------------------- review of #157: no success for a sync that never ran

   [Fact]
   public async Task RunAsync_AnAccountWhoseOwnTokenIsGone_IsSkipped_NotReportedAsSucceeded()
   {
      WorkspaceViewModel workspace = CreateWorkspace("own");
      await workspace.LoadReposCommand.ExecuteAsync(null); // a loaded table: the sync command can run without a token
      _vault.Delete(workspace.AccountId!.Value);
      var states = new List<SyncAllAccountState>();
      _coordinator.AccountStateChanged = (_, state) => states.Add(state);

      SyncAllResult result = await _coordinator.RunAsync([workspace], CancellationToken.None);

      Assert.Equal(0, result.Ran);
      Assert.Equal(1, result.Skipped);
      Assert.Equal([SyncAllAccountState.Queued, SyncAllAccountState.Skipped], states);
   }



   [Fact]
   public async Task RunAsync_ASyncThatCouldNotStart_IsFailed_NotSucceeded_AndIsNotCountedAsRun()
   {
      // The target folder cannot be used: a file sits where it should be.
      WorkspaceViewModel workspace = CreateWorkspace("blocked");
      Directory.CreateDirectory(_root);
      File.WriteAllText(Path.Combine(_root, "blocked"), "a file, not a folder");
      var states = new List<SyncAllAccountState>();
      _coordinator.AccountStateChanged = (_, state) => states.Add(state);

      SyncAllResult result = await _coordinator.RunAsync([workspace], CancellationToken.None);

      Assert.Equal(RunResultKind.Error, workspace.ResultKind);
      Assert.Equal(0, result.Ran);
      Assert.Equal(1, result.Skipped);
      Assert.Equal([SyncAllAccountState.Queued, SyncAllAccountState.Running, SyncAllAccountState.Failed], states);
   }



   // ---------------------------------------------------------------- the status a bar shows (#167)

   /// <summary>A clone that waits until the test lets it go, and says when it was entered.</summary>
   private static (FakeGitClient Git, Task Entered, TaskCompletionSource Gate) GatedGit()
   {
      var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var git = new FakeGitClient
      {
         CloneHandler = (_, _, _, _, _) =>
         {
            entered.TrySetResult();
            return gate.Task;
         },
      };
      return (git, entered.Task, gate);
   }



   [Fact]
   public async Task TheStatus_SaysWhichAccountIsRunning_HowFarItIs_AndHowThePassEnded()
   {
      (FakeGitClient git, Task entered, TaskCompletionSource gate) = GatedGit();
      WorkspaceViewModel alpha = CreateWorkspace("alpha", git);
      WorkspaceViewModel beta = CreateWorkspace("beta");
      var status = new SyncAllStatus();
      _coordinator.Status = status;
      var said = new List<string>();
      status.PropertyChanged += (_, e) =>
      {
         if(string.Equals(e.PropertyName, nameof(SyncAllStatus.Message), StringComparison.Ordinal))
         {
            said.Add(status.Message);
         }
      };
      Assert.False(status.IsOpen);
      Assert.Equal(SyncAllOutcome.None, status.Outcome);
      Assert.Equal("", status.Title);

      Task<SyncAllResult> run = _coordinator.RunAsync([alpha, beta], CancellationToken.None);
      await entered.WaitAsync(TimeSpan.FromSeconds(30));

      Assert.True(status.IsRunning);
      Assert.True(status.IsOpen);
      Assert.Equal(SyncAllOutcome.Running, status.Outcome);
      Assert.Equal("Sync all: account 1 of 2", status.Title);
      Assert.Equal("alpha: 0 of 1 repository.", status.Message);
      Assert.Equal(alpha.AccountId, status.CurrentAccountId);
      Assert.Equal(0d, status.Fraction);

      gate.SetResult();
      SyncAllResult result = await run.WaitAsync(TimeSpan.FromSeconds(30));

      Assert.Equal(new SyncAllResult(Ran: 2, Skipped: 0, WasCanceled: false), result);
      Assert.False(status.IsRunning);
      Assert.True(status.IsOpen); // the result stays until it is closed
      Assert.Equal(SyncAllOutcome.Finished, status.Outcome);
      Assert.Equal("Sync all finished", status.Title);
      Assert.Equal("2 accounts synced, 0 skipped.", status.Message);
      Assert.Null(status.CurrentAccountId);
      Assert.Null(status.Fraction);

      // Every step was said, in order: the start, each account's listing and its count.
      Assert.Equal(
          [
             "Starting 2 accounts.",
             "alpha: starting.",
             "alpha: listing its repositories.",
             "alpha: starting.",
             "alpha: 0 of 1 repository.",
             "alpha: 1 of 1 repository.",
             "alpha: starting.",
             "beta: starting.",
             "beta: listing its repositories.",
             "beta: starting.",
             "beta: 0 of 1 repository.",
             "beta: 1 of 1 repository.",
             "beta: starting.",
             "2 accounts synced, 0 skipped.",
          ],
          said);

      // The pass is over: what the accounts do next is no longer the bar's business.
      status.IsOpen = false;
      await alpha.SyncCommand.ExecuteAsync(null);
      Assert.Equal("2 accounts synced, 0 skipped.", status.Message);
      Assert.False(status.IsOpen);
   }



   [Fact]
   public async Task TheStatus_ShowsSeveralRepositories_AsAFraction()
   {
      (FakeGitClient git, Task entered, TaskCompletionSource gate) = GatedGit();
      _lister.Handler = org => [Repo(org + "-one"), Repo(org + "-two")];
      WorkspaceViewModel alpha = CreateWorkspace("alpha", git);
      alpha.MaxConcurrencyValue = 1;
      var status = new SyncAllStatus();
      _coordinator.Status = status;
      var fractions = new List<double?>();
      status.PropertyChanged += (_, e) =>
      {
         if(string.Equals(e.PropertyName, nameof(SyncAllStatus.Fraction), StringComparison.Ordinal))
         {
            fractions.Add(status.Fraction);
         }
      };

      Task<SyncAllResult> run = _coordinator.RunAsync([alpha], CancellationToken.None);
      await entered.WaitAsync(TimeSpan.FromSeconds(30));
      Assert.Equal("Sync all: account 1 of 1", status.Title);
      Assert.Equal("alpha: 0 of 2 repositories.", status.Message);
      gate.SetResult();
      await run.WaitAsync(TimeSpan.FromSeconds(30));

      Assert.Contains(0.5, fractions);
      Assert.Equal("1 account synced, 0 skipped.", status.Message);
   }



   [Fact]
   public async Task ACancelRequest_IsSaidAtOnce_AndThePassEndsAsCanceled()
   {
      (FakeGitClient git, Task entered, TaskCompletionSource gate) = GatedGit();
      WorkspaceViewModel alpha = CreateWorkspace("alpha", git);
      WorkspaceViewModel beta = CreateWorkspace("beta");
      var status = new SyncAllStatus();
      _coordinator.Status = status;
      using var cts = new CancellationTokenSource();

      status.RequestCancel(); // nothing runs: nothing to cancel
      Assert.False(status.IsCancelRequested);

      Task<SyncAllResult> run = _coordinator.RunAsync([alpha, beta], cts.Token);
      await entered.WaitAsync(TimeSpan.FromSeconds(30));
      cts.Cancel();
      status.RequestCancel();

      Assert.True(status.IsCancelRequested);
      Assert.Equal("alpha: 0 of 1 repository. Stopping when this account is done.", status.Message);

      gate.SetResult();
      SyncAllResult result = await run.WaitAsync(TimeSpan.FromSeconds(30));

      Assert.True(result.WasCanceled);
      Assert.Equal(SyncAllOutcome.Canceled, status.Outcome);
      Assert.Equal("Sync all canceled", status.Title);
      Assert.Equal("1 account synced, 0 skipped.", status.Message);
      Assert.False(status.IsRunning);

      // The next pass starts clean.
      (await _coordinator.RunAsync([beta], CancellationToken.None)).ToString();
      Assert.False(status.IsCancelRequested);
      Assert.Equal(SyncAllOutcome.Finished, status.Outcome);
   }



   [Fact]
   public async Task AccountsThatFailed_AreCounted_AndThePassEndsWithFailures()
   {
      // One whose repository fails, one that cannot start at all, one that is fine.
      WorkspaceViewModel failing = CreateWorkspace(
          "alpha", new FakeGitClient { CloneHandler = (_, _, _, _, _) => Task.FromException(new InvalidOperationException("boom")) });
      WorkspaceViewModel blocked = CreateWorkspace("blocked");
      Directory.CreateDirectory(_root);
      File.WriteAllText(Path.Combine(_root, "blocked"), "a file, not a folder");
      WorkspaceViewModel fine = CreateWorkspace("gamma");
      var status = new SyncAllStatus();
      _coordinator.Status = status;

      SyncAllResult result = await _coordinator.RunAsync([failing, blocked, fine], CancellationToken.None);

      Assert.Equal(new SyncAllResult(Ran: 2, Skipped: 1, WasCanceled: false, Failed: 2), result);
      Assert.Equal(SyncAllOutcome.FinishedWithFailures, status.Outcome);
      Assert.Equal("Sync all finished", status.Title);
      Assert.Equal("2 accounts synced, 1 skipped. 2 accounts need attention; they are marked in the pane.", status.Message);
   }



   [Fact]
   public async Task ACanceledPass_KeepsTheFailuresItSaw()
   {
      WorkspaceViewModel failing = CreateWorkspace(
          "alpha", new FakeGitClient { CloneHandler = (_, _, _, _, _) => Task.FromException(new InvalidOperationException("boom")) });
      WorkspaceViewModel never = CreateWorkspace("beta");
      using var cts = new CancellationTokenSource();
      _coordinator.AccountStateChanged = (_, state) =>
      {
         if(state == SyncAllAccountState.Failed)
         {
            cts.Cancel(); // as soon as the first account is done
         }
      };

      SyncAllResult result = await _coordinator.RunAsync([failing, never], cts.Token);

      Assert.Equal(new SyncAllResult(Ran: 1, Skipped: 0, WasCanceled: true, Failed: 1), result);
      Assert.Equal("1 account synced, 0 skipped. 1 account needs attention; it is marked in the pane.", SyncAllStatus.Describe(result));
   }



   [Fact]
   public async Task AnUnexpectedError_EndsTheStatusToo_InsteadOfLeavingItRunning()
   {
      WorkspaceViewModel alpha = CreateWorkspace("alpha");
      var status = new SyncAllStatus();
      _coordinator.Status = status;
      _coordinator.AccountStateChanged = (_, _) => throw new InvalidOperationException("the pane is gone");

      await Assert.ThrowsAsync<InvalidOperationException>(() => _coordinator.RunAsync([alpha], CancellationToken.None));

      Assert.False(status.IsRunning);
      Assert.True(status.IsOpen);
      Assert.Equal(SyncAllOutcome.Stopped, status.Outcome);
      Assert.Equal("Sync all stopped", status.Title);
      Assert.Equal("the pane is gone", status.Message);
   }



   [Fact]
   public async Task ASecondPass_WithFewerRepositoriesSelected_NeverShowsMoreDoneThanThereIsToDo()
   {
      _lister.Handler = org => [Repo(org + "-one"), Repo(org + "-two"), Repo(org + "-three")];
      WorkspaceViewModel alpha = CreateWorkspace("alpha");
      var status = new SyncAllStatus();
      _coordinator.Status = status;
      await _coordinator.RunAsync([alpha], CancellationToken.None); // three of three
      alpha.Repos[1].IsSelected = false;
      alpha.Repos[2].IsSelected = false;
      var fractions = new List<double?>();
      var said = new List<string>();
      status.PropertyChanged += (_, e) =>
      {
         if(string.Equals(e.PropertyName, nameof(SyncAllStatus.Fraction), StringComparison.Ordinal))
         {
            fractions.Add(status.Fraction);
         }
         else if(string.Equals(e.PropertyName, nameof(SyncAllStatus.Message), StringComparison.Ordinal))
         {
            said.Add(status.Message);
         }
      };

      await _coordinator.RunAsync([alpha], CancellationToken.None); // one of one; the run before left 3 of 3 behind

      Assert.All(fractions, fraction => Assert.True(fraction is null or (>= 0 and <= 1), $"fraction {fraction}"));
      Assert.DoesNotContain(said, message => message.Contains("3 of 1", StringComparison.Ordinal));
      Assert.Contains("alpha: 1 of 1 repository.", said, StringComparer.Ordinal);
   }



   [Fact]
   public void ACancelRequest_BeforeTheFirstAccountHasStarted_IsRemembered()
   {
      var status = new SyncAllStatus();
      status.Begin(1);
      Assert.Equal("Starting 1 account.", status.Message);

      status.RequestCancel();

      Assert.True(status.IsCancelRequested);
      Assert.Equal("Starting 1 account.", status.Message); // no account to name yet
   }



   [Fact]
   public void TheStatus_RefusesAMissingWorkspaceOrResult()
   {
      var status = new SyncAllStatus();

      Assert.Throws<ArgumentNullException>(() => status.Enter(1, null!));
      Assert.Throws<ArgumentNullException>(() => status.End(null!));
   }
}
