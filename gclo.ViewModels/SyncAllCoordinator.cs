using gclo.Engine;

namespace gclo.ViewModels;

/// <summary>
/// Outcome of one sync-all pass over the account workspaces.
/// </summary>
/// <param name="Ran">Accounts whose sync ran to completion (successfully or not).</param>
/// <param name="Skipped">Accounts skipped because they were busy, could not load, or had nothing to sync.</param>
/// <param name="WasCanceled">True when cancellation stopped the queue before every account was processed.</param>
public sealed record SyncAllResult(int Ran, int Skipped, bool WasCanceled);

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

        int ran = 0;
        int skipped = 0;

        foreach (WorkspaceViewModel workspace in accountWorkspaces)
        {
            if (ct.IsCancellationRequested)
            {
                _log.Info("Sync all: canceled — remaining accounts were not started.");
                return new SyncAllResult(ran, skipped, WasCanceled: true);
            }

            if (workspace.IsRunning || workspace.IsLoadingRepos || workspace.IsResolvingPaths)
            {
                skipped++;
                _log.Info($"Sync all: skipped '{workspace.DisplayName}' — it is busy.");
                continue;
            }

            if (!workspace.HasLoadedRepos)
            {
                _log.Info($"Sync all: loading repositories for '{workspace.DisplayName}'.");
                await workspace.LoadReposCommand.ExecuteAsync(null);
                if (!workspace.HasLoadedRepos)
                {
                    skipped++;
                    _log.Info(
                        $"Sync all: skipped '{workspace.DisplayName}' — its repositories could not be loaded.");
                    continue;
                }
            }

            if (!workspace.SyncCommand.CanExecute(null))
            {
                skipped++;
                _log.Info($"Sync all: skipped '{workspace.DisplayName}' — it has nothing to sync.");
                continue;
            }

            _log.Info($"Sync all: syncing '{workspace.DisplayName}'.");
            await workspace.SyncCommand.ExecuteAsync(null);
            ran++;
            _log.Info($"Sync all: finished '{workspace.DisplayName}'.");
        }

        return new SyncAllResult(ran, skipped, WasCanceled: false);
    }
}
