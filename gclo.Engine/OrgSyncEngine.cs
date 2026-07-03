using System.Collections.Concurrent;

namespace gclo.Engine;

/// <summary>
/// Clones or updates every repository in a GitHub organization with bounded parallelism.
/// One repository failing never aborts the others; cancellation stops scheduling and
/// marks unprocessed repositories as <see cref="SyncStatus.Canceled"/>.
/// </summary>
public sealed class OrgSyncEngine
{
    private readonly IRepositoryLister _lister;
    private readonly IGitClient _git;

    public OrgSyncEngine(IRepositoryLister lister, IGitClient git)
    {
        _lister = lister ?? throw new ArgumentNullException(nameof(lister));
        _git = git ?? throw new ArgumentNullException(nameof(git));
    }

    /// <summary>Lists the organization's repositories, then syncs all of them.</summary>
    public async Task<SyncSummary> SyncAsync(
        SyncRequest request,
        IProgress<RepoProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Organization);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Token);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetRoot);

        var repositories = await _lister
            .ListOrganizationRepositoriesAsync(request.Organization, request.Token, cancellationToken)
            .ConfigureAwait(false);

        return await SyncAsync(request, repositories, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Syncs a caller-supplied repository list without hitting the GitHub API.
    /// Use this when the repositories were already listed (and possibly filtered
    /// down to a selection) by the caller; semantics are otherwise identical to
    /// <see cref="SyncAsync(SyncRequest, IProgress{RepoProgress}?, CancellationToken)"/>.
    /// </summary>
    public async Task<SyncSummary> SyncAsync(
        SyncRequest request,
        IReadOnlyList<RepoDescriptor> repositories,
        IProgress<RepoProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(repositories);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Token);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetRoot);

        var repos = repositories
            // Names key the pending dictionary and the target folders; a duplicate
            // (possible from pagination shifts) must not crash the run or race two
            // git operations into the same directory.
            .DistinctBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Create the root before anything is reported as Queued: if the path is
        // invalid, the caller gets one exception instead of repos stranded mid-state.
        Directory.CreateDirectory(request.TargetRoot);

        foreach (var repo in repos)
        {
            progress?.Report(new RepoProgress(repo.Name, SyncStatus.Queued));
        }

        if (repos.Count == 0)
        {
            return new SyncSummary(0, 0, 0, 0, 0, WasCanceled: false);
        }

        int cloned = 0, updated = 0, failed = 0, canceled = 0;
        var pending = new ConcurrentDictionary<string, RepoDescriptor>(
            repos.Select(r => new KeyValuePair<string, RepoDescriptor>(r.Name, r)));

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, request.MaxConcurrency),
            CancellationToken = cancellationToken,
        };

        try
        {
            // Parallel.ForEachAsync stops scheduling as soon as any body throws, so the
            // body must swallow every per-repo failure; only cancellation may escape.
            await Parallel.ForEachAsync(repos, parallelOptions, async (repo, token) =>
            {
                var path = Path.Combine(request.TargetRoot, repo.Name);
                try
                {
                    token.ThrowIfCancellationRequested();

                    if (_git.IsValidRepository(path))
                    {
                        progress?.Report(new RepoProgress(repo.Name, SyncStatus.Pulling));
                        await _git.FetchAndPullAsync(path, request.Token, token).ConfigureAwait(false);
                        Interlocked.Increment(ref updated);
                    }
                    else
                    {
                        progress?.Report(new RepoProgress(repo.Name, SyncStatus.Cloning));
                        await _git.CloneAsync(
                            repo.CloneUrl, path, request.Token,
                            pct => progress?.Report(new RepoProgress(repo.Name, SyncStatus.Cloning, Percent: pct)),
                            token).ConfigureAwait(false);
                        Interlocked.Increment(ref cloned);
                    }

                    pending.TryRemove(repo.Name, out _);
                    progress?.Report(new RepoProgress(repo.Name, SyncStatus.Done));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    Interlocked.Increment(ref canceled);
                    pending.TryRemove(repo.Name, out _);
                    progress?.Report(new RepoProgress(repo.Name, SyncStatus.Canceled));
                    throw; // stop the loop; unstarted repos are marked Canceled below
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    pending.TryRemove(repo.Name, out _);
                    progress?.Report(new RepoProgress(repo.Name, SyncStatus.Failed, ex.Message));
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            foreach (var repo in pending.Values)
            {
                canceled++;
                progress?.Report(new RepoProgress(repo.Name, SyncStatus.Canceled));
            }
        }

        return new SyncSummary(repos.Count, cloned, updated, failed, canceled, cancellationToken.IsCancellationRequested);
    }
}
