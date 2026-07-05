using gclo.Engine;

namespace gclo.ViewModels;

/// <summary>
/// Outcome of one sync-all pass over the account workspaces.
/// </summary>
/// <param name="Ran">Accounts whose sync ran to completion (successfully or not).</param>
/// <param name="Skipped">Accounts skipped because they were busy, could not load, or had nothing to sync.</param>
/// <param name="WasCanceled">True when cancellation stopped the queue before every account was processed.</param>
public sealed record SyncAllResult(int Ran, int Skipped, bool WasCanceled);

/// <summary>Where one account stands within a sync-all pass; feeds the sidebar badges.</summary>
public enum SyncAllAccountState
{
    /// <summary>Scheduled in this pass, waiting its turn.</summary>
    Queued,

    /// <summary>The one account whose load/sync is in flight right now.</summary>
    Running,

    /// <summary>Finished with no failed repositories.</summary>
    Succeeded,

    /// <summary>Finished, but at least one repository failed.</summary>
    Failed,

    /// <summary>Not run: busy, unloadable, nothing to sync, or the queue was canceled.</summary>
    Skipped,
}

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
    /// Syncs each workspace of <paramref name="accountWorkspaces"/> in list order,
    /// loading its repositories first when needed. A workspace is skipped (counted,
    /// logged, never fatal) when it is already busy, when its repositories cannot be
    /// loaded, or when its sync command cannot run. Canceling <paramref name="ct"/>
    /// stops the queue after the in-flight account rather than canceling its sync.
    /// </summary>
    public async Task<SyncAllResult> RunAsync(
        IReadOnlyList<WorkspaceViewModel> accountWorkspaces, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(accountWorkspaces);

        foreach (WorkspaceViewModel workspace in accountWorkspaces)
        {
            Announce(workspace, SyncAllAccountState.Queued);
        }

        int ran = 0;
        int skipped = 0;

        for (int i = 0; i < accountWorkspaces.Count; i++)
        {
            WorkspaceViewModel workspace = accountWorkspaces[i];

            if (ct.IsCancellationRequested)
            {
                // Unstarted accounts leave the pass as Skipped badges, but the result's
                // Skipped count keeps its meaning: accounts actively skipped, not the
                // abandoned remainder of a canceled queue.
                for (int rest = i; rest < accountWorkspaces.Count; rest++)
                {
                    Announce(accountWorkspaces[rest], SyncAllAccountState.Skipped);
                }
                _log.Info("Sync all: canceled — remaining accounts were not started.");
                return new SyncAllResult(ran, skipped, WasCanceled: true);
            }

            if (workspace.IsRunning || workspace.IsLoadingRepos || workspace.IsResolvingPaths)
            {
                skipped++;
                Announce(workspace, SyncAllAccountState.Skipped);
                _log.Info($"Sync all: skipped '{workspace.DisplayName}' — it is busy.");
                continue;
            }

            Announce(workspace, SyncAllAccountState.Running);

            if (!workspace.HasLoadedRepos)
            {
                _log.Info($"Sync all: loading repositories for '{workspace.DisplayName}'.");
                await workspace.LoadReposCommand.ExecuteAsync(null);
                if (!workspace.HasLoadedRepos)
                {
                    skipped++;
                    Announce(workspace, SyncAllAccountState.Skipped);
                    _log.Info(
                        $"Sync all: skipped '{workspace.DisplayName}' — its repositories could not be loaded.");
                    continue;
                }
            }

            if (!workspace.SyncCommand.CanExecute(null))
            {
                skipped++;
                Announce(workspace, SyncAllAccountState.Skipped);
                _log.Info($"Sync all: skipped '{workspace.DisplayName}' — it has nothing to sync.");
                continue;
            }

            _log.Info($"Sync all: syncing '{workspace.DisplayName}'.");
            await workspace.SyncCommand.ExecuteAsync(null);
            ran++;
            Announce(
                workspace,
                workspace.HasFailedRepos ? SyncAllAccountState.Failed : SyncAllAccountState.Succeeded);
            _log.Info($"Sync all: finished '{workspace.DisplayName}'.");
        }

        return new SyncAllResult(ran, skipped, WasCanceled: false);
    }

    private void Announce(WorkspaceViewModel workspace, SyncAllAccountState state)
    {
        if (workspace.AccountId is Guid id)
        {
            AccountStateChanged?.Invoke(id, state);
        }
    }
}
