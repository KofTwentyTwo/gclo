/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.Engine;


namespace gclo.ViewModels;


/// <summary>
/// Runs every account workspace's sync strictly one at a time, in the order given.
/// Workspaces that have not loaded their repositories yet are loaded first; busy or
/// unloadable workspaces are skipped rather than failing the pass. Cancellation is
/// cooperative and checked between accounts only: the in-flight account always
/// finishes, and just the remaining queue is abandoned.
/// </summary>
/// <param name="log">Sink for per-account start/finish/skip events. Never receives tokens.</param>
public sealed class SyncAllCoordinator(IActivityLog log)
{
   private readonly IActivityLog _log = log ?? throw new ArgumentNullException(nameof(log));

   /// <summary>
   /// Per-account queue-state announcements for this pass, keyed by the workspace's
   /// account id. Every scheduled account is announced Queued up front, Running when
   /// its turn starts, then exactly one terminal state (Succeeded, Failed, Skipped).
   /// Raised on the caller's context; the view maps these to sidebar badges.
   /// </summary>
   public Action<Guid, SyncAllAccountState>? AccountStateChanged { get; set; }

   /// <summary>
   /// Told where the pass stands, from its start to its end, so a status bar can show
   /// it whichever workspace is on screen (#167). Updated on the caller's context.
   /// </summary>
   public SyncAllStatus? Status { get; set; }



   /// <summary>
   /// Syncs each workspace of <paramref name="accountWorkspaces"/> in list order,
   /// loading its repositories first when needed. A workspace is skipped (counted,
   /// logged, never fatal) when it is already busy or has nothing to sync. One that
   /// cannot be synced (it has no token, its repositories cannot be loaded, its sync
   /// cannot start) is left out too, and counted and announced as failed, not as
   /// skipped: the pass must not read as a success when an account was not synced
   /// (#182). Canceling <paramref name="ct"/> stops the queue after the in-flight
   /// account rather than canceling its sync.
   /// </summary>
   public async Task<SyncAllResult> RunAsync(
       IReadOnlyList<WorkspaceViewModel> accountWorkspaces, CancellationToken ct)
   {
      ArgumentNullException.ThrowIfNull(accountWorkspaces);

      Status?.Begin(accountWorkspaces.Count);
      try
      {
         SyncAllResult result = await RunQueueAsync(accountWorkspaces, ct);
         Status?.End(result);
         return result;
      }
      catch(Exception ex)
      {
         // Not expected (every account's trouble is a skip, not a throw), but a bar
         // that says 'running' for ever would be worse than the error itself.
         Status?.Stop(ex.Message);
         throw;
      }
   }



   private async Task<SyncAllResult> RunQueueAsync(
       IReadOnlyList<WorkspaceViewModel> accountWorkspaces, CancellationToken ct)
   {
      foreach(WorkspaceViewModel workspace in accountWorkspaces)
      {
         Announce(workspace, SyncAllAccountState.Queued);
      }
      _log.Info($"Sync all: queued {accountWorkspaces.Count} accounts: "
          + string.Join(", ", accountWorkspaces.Select(w => $"'{w.DisplayName}'")) + ".");

      int ran = 0;
      int skipped = 0;
      int failed = 0;
      int couldNotSync = 0;

      for(int i = 0; i < accountWorkspaces.Count; i++)
      {
         WorkspaceViewModel workspace = accountWorkspaces[i];

         if(ct.IsCancellationRequested)
         {
            // Unstarted accounts leave the pass as Skipped badges, but the result's
            // Skipped count keeps its meaning: accounts actively skipped, not the
            // abandoned remainder of a canceled queue.
            for(int rest = i; rest < accountWorkspaces.Count; rest++)
            {
               Announce(accountWorkspaces[rest], SyncAllAccountState.Skipped);
            }
            _log.Info("Sync all: canceled — remaining accounts were not started.");
            return new SyncAllResult(ran, skipped, WasCanceled: true, failed, NotStarted: accountWorkspaces.Count - i, couldNotSync);
         }

         if(workspace.IsRunning || workspace.IsLoadingRepos || workspace.IsResolvingPaths)
         {
            skipped++;
            Announce(workspace, SyncAllAccountState.Skipped);
            _log.Info($"Sync all: skipped '{workspace.DisplayName}' — it is busy.");
            continue;
         }

         // A token saved, replaced, or removed since the workspace was opened counts.
         workspace.RefreshAccountToken();
         if(workspace.MissingTokenDescription is { } missingToken)
         {
            // Whichever source the account's token comes from, it holds none: say
            // which, instead of a sync that cannot start or a generic auth failure
            // that sends the user to the wrong place.
            couldNotSync++;
            failed++;
            Announce(workspace, SyncAllAccountState.Failed);
            _log.Info($"Sync all: '{workspace.DisplayName}' could not be synced — {missingToken}");
            continue;
         }

         Announce(workspace, SyncAllAccountState.Running);
         Status?.Enter(i + 1, workspace);

         if(!workspace.HasLoadedRepos)
         {
            _log.Info($"Sync all: loading repositories for '{workspace.DisplayName}'.");
            await workspace.LoadReposCommand.ExecuteAsync(null);
            if(!workspace.HasLoadedRepos)
            {
               couldNotSync++;
               failed++;
               Announce(workspace, SyncAllAccountState.Failed);
               _log.Info(
                   $"Sync all: '{workspace.DisplayName}' could not be synced — its repositories could not be loaded.");
               continue;
            }
         }

         if(!workspace.SyncCommand.CanExecute(null))
         {
            skipped++;
            Announce(workspace, SyncAllAccountState.Skipped);
            _log.Info($"Sync all: skipped '{workspace.DisplayName}' — it has nothing to sync.");
            continue;
         }

         _log.Info($"Sync all: syncing '{workspace.DisplayName}'.");
         await workspace.SyncCommand.ExecuteAsync(null);
         if(workspace.ResultKind == RunResultKind.Error)
         {
            // The sync never ran its repositories (its target folder could not be
            // used, its token went away at that moment): that is not a success with
            // nothing to report.
            couldNotSync++;
            failed++;
            Announce(workspace, SyncAllAccountState.Failed);
            _log.Info($"Sync all: '{workspace.DisplayName}' could not be synced — {workspace.ResultMessage}");
            continue;
         }

         if(workspace.ResultKind == RunResultKind.Canceled)
         {
            ///////////////////////////////////////////////////////////////////////////
            // Its own sync was canceled (in its workspace, or because the account  //
            // was edited meanwhile) and left repositories unsynced: that is not a  //
            // synced account. The pass itself goes on.                             //
            ///////////////////////////////////////////////////////////////////////////
            if(workspace.HasFailedRepos)
            {
               couldNotSync++;
               failed++;
            }
            else
            {
               skipped++;
            }
            Announce(workspace, workspace.HasFailedRepos ? SyncAllAccountState.Failed : SyncAllAccountState.Skipped);
            _log.Info($"Sync all: '{workspace.DisplayName}' was canceled before it finished.");
            continue;
         }

         ran++;
         if(workspace.HasFailedRepos)
         {
            failed++;
         }
         Announce(
             workspace,
             workspace.HasFailedRepos ? SyncAllAccountState.Failed : SyncAllAccountState.Succeeded);
         _log.Info($"Sync all: finished '{workspace.DisplayName}'.");
      }

      return new SyncAllResult(ran, skipped, WasCanceled: false, failed, CouldNotSync: couldNotSync);
   }



   private void Announce(WorkspaceViewModel workspace, SyncAllAccountState state)
   {
      if(workspace.AccountId is Guid id)
      {
         AccountStateChanged?.Invoke(id, state);
      }
   }
}
