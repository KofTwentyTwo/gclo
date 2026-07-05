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
        foreach (WorkspaceViewModel workspace in _workspaces)
        {
            workspace.Dispose();
        }
        TryDeleteDirectory(_root);
    }

    /// <summary>Synchronous progress: handler runs inline on the reporting thread.</summary>
    private sealed class SyncProgress(Action<RepoProgress> handler) : IProgress<RepoProgress>
    {
        public void Report(RepoProgress value) => handler(value);
    }

    /// <summary>
    /// One lister shared by every workspace, so the recorded organization order proves
    /// globally which account loaded when. Behavior is scripted per organization.
    /// </summary>
    private sealed class OrderedLister : IRepositoryLister
    {
        private readonly List<string> _organizations = new();
        private readonly object _gate = new();

        /// <summary>Repositories (or exception) per organization. Default: one repo named '&lt;org&gt;-repo'.</summary>
        public Func<string, IReadOnlyList<RepoDescriptor>> Handler { get; set; }
            = org => new[] { Repo(org + "-repo") };

        /// <summary>Organizations listed so far, in call order.</summary>
        public IReadOnlyList<string> Organizations
        {
            get
            {
                lock (_gate)
                {
                    return _organizations.ToArray();
                }
            }
        }

        public Task<IReadOnlyList<RepoDescriptor>> ListOrganizationRepositoriesAsync(
            string organization, string token, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _organizations.Add(organization);
            }
            try
            {
                return Task.FromResult(Handler(organization));
            }
            catch (Exception ex)
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
        var alpha = CreateWorkspace("alpha", gitAlpha);
        var beta = CreateWorkspace("beta");

        Task<SyncAllResult> run = _coordinator.RunAsync(new[] { alpha, beta }, CancellationToken.None);

        await entered.Task; // alpha's sync is in flight...
        Assert.Equal(new[] { "org-alpha" }, _lister.Organizations); // ...and beta has not started

        gate.SetResult();
        SyncAllResult result = await run;

        Assert.Equal(new SyncAllResult(Ran: 2, Skipped: 0, WasCanceled: false), result);
        Assert.Equal(new[] { "org-alpha", "org-beta" }, _lister.Organizations);
    }

    // ---------------------------------------------------------------- load-before-sync

    [Fact]
    public async Task RunAsync_LoadsAnUnloadedWorkspace_ThenSyncsIt()
    {
        var git = new FakeGitClient();
        var workspace = CreateWorkspace("alpha", git);
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
        var workspace = CreateWorkspace("alpha", git);
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
        var running = CreateWorkspace("alpha");
        running.IsRunning = true;
        var loading = CreateWorkspace("beta");
        loading.IsLoadingRepos = true;
        var resolving = CreateWorkspace("gamma");
        resolving.IsResolvingPaths = true;
        var git = new FakeGitClient();
        var idle = CreateWorkspace("delta", git);

        SyncAllResult result = await _coordinator.RunAsync(
            new[] { running, loading, resolving, idle }, CancellationToken.None);

        Assert.Equal(new SyncAllResult(Ran: 1, Skipped: 3, WasCanceled: false), result);
        Assert.Equal(new[] { "org-delta" }, _lister.Organizations); // busy ones were never even loaded
        Assert.Equal(new[] { "org-delta-repo" }, git.ClonedRepoNames);
    }

    [Fact]
    public async Task RunAsync_SkipsAWorkspaceWhoseLoadFails_AndMovesOn()
    {
        _lister.Handler = org => org == "org-alpha"
            ? throw new InvalidOperationException("org not found")
            : new[] { Repo(org + "-repo") };
        var alpha = CreateWorkspace("alpha");
        var git = new FakeGitClient();
        var beta = CreateWorkspace("beta", git);

        SyncAllResult result = await _coordinator.RunAsync(new[] { alpha, beta }, CancellationToken.None);

        Assert.Equal(new SyncAllResult(Ran: 1, Skipped: 1, WasCanceled: false), result);
        Assert.False(alpha.HasLoadedRepos);
        Assert.Equal(new[] { "org-beta-repo" }, git.ClonedRepoNames);
    }

    [Fact]
    public async Task RunAsync_SkipsALoadedWorkspaceWhoseSyncCannotRun()
    {
        _lister.Handler = _ => Array.Empty<RepoDescriptor>();
        var workspace = CreateWorkspace("alpha");

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
        var alpha = CreateWorkspace("alpha", gitAlpha);
        var gitBeta = new FakeGitClient();
        var beta = CreateWorkspace("beta", gitBeta);
        using var cts = new CancellationTokenSource();

        Task<SyncAllResult> run = _coordinator.RunAsync(new[] { alpha, beta }, cts.Token);
        await entered.Task;
        cts.Cancel(); // requested while alpha's sync is in flight...
        gate.SetResult(); // ...which must still run to completion
        SyncAllResult result = await run;

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
        var workspace = CreateWorkspace("alpha", git);
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
        var alpha = CreateWorkspace("alpha");
        var beta = CreateWorkspace("beta");
        var states = RecordStates();

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
        var workspace = CreateWorkspace("alpha", git);
        var states = RecordStates();

        await _coordinator.RunAsync(new[] { workspace }, CancellationToken.None);

        Assert.Equal(SyncAllAccountState.Failed, states[^1].State);
    }

    [Fact]
    public async Task RunAsync_BusyAccount_AnnouncesSkippedWithoutRunning()
    {
        var workspace = CreateWorkspace("alpha");
        workspace.IsResolvingPaths = true; // busy: must be skipped
        var states = RecordStates();

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
        var alpha = CreateWorkspace("alpha");
        var beta = CreateWorkspace("beta");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var states = RecordStates();

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
}
