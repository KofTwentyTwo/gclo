using gclo.Engine;
using gclo.ViewModels;
using static gclo.Engine.Tests.GitTestHelpers;

namespace gclo.Engine.Tests;

/// <summary>
/// Headless tests for <see cref="WorkspaceViewModel"/> using a synchronous progress factory
/// (the production default, <see cref="Progress{T}"/>, posts asynchronously and would
/// make assertions racy) and a near-zero org-lookup debounce.
/// </summary>
public sealed class WorkspaceViewModelTests
{
    private readonly FakeRepositoryLister _lister = new();
    private readonly FakeGitClient _git = new();
    private readonly FakeOrganizationLister _orgs = new();

    /// <summary>Synchronous progress: handler runs inline on the reporting thread.</summary>
    private sealed class SyncProgress(Action<RepoProgress> handler) : IProgress<RepoProgress>
    {
        public void Report(RepoProgress value) => handler(value);
    }

    /// <summary>A lister that blocks until the test releases <see cref="Gate"/>.</summary>
    private sealed class GatedLister : IRepositoryLister
    {
        public TaskCompletionSource<IReadOnlyList<RepoDescriptor>> Gate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<RepoDescriptor>> ListOrganizationRepositoriesAsync(
            string organization, string token, CancellationToken cancellationToken = default)
            => Gate.Task;
    }

    private WorkspaceViewModel CreateViewModel(
        TimeSpan? debounce = null,
        Account? account = null,
        ITokenVault? vault = null,
        AccountsStore? store = null)
        => new(_lister, _git, _orgs,
               handler => new SyncProgress(handler),
               debounce ?? TimeSpan.FromMilliseconds(1),
               new NullActivityLog(),
               account, vault, store);

    /// <summary>
    /// A view model with valid inputs and the given repositories already loaded into the
    /// table. Waits out the token-triggered org lookup first so its status message cannot
    /// land on top of a later assertion.
    /// </summary>
    private async Task<WorkspaceViewModel> CreateLoadedViewModelAsync(params RepoDescriptor[] repos)
    {
        _lister.Repositories = repos;
        var vm = CreateViewModel();
        vm.Organization = "acme";
        vm.Token = "token-1234567890";
        await WaitUntilAsync(() => vm.StatusText.Length > 0, "org lookup to settle");
        vm.TargetFolder = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));
        vm.MaxConcurrencyValue = 1; // serialize progress: the sync fake reports inline across threads

        await vm.LoadReposCommand.ExecuteAsync(null);
        return vm;
    }

    // ---------------------------------------------------------------- command gating

    [Fact]
    public void LoadReposCommand_RequiresOrganizationAndToken()
    {
        var vm = CreateViewModel();
        Assert.False(vm.LoadReposCommand.CanExecute(null));

        vm.Organization = "acme";
        Assert.False(vm.LoadReposCommand.CanExecute(null));

        vm.Token = "token-1234567890";
        Assert.True(vm.LoadReposCommand.CanExecute(null));
    }

    [Fact]
    public void SyncCommand_DisabledBeforeAnyReposLoaded()
    {
        var vm = CreateViewModel();
        vm.Organization = "acme";
        vm.Token = "token-1234567890";
        vm.TargetFolder = @"C:\src\acme";

        Assert.False(vm.SyncCommand.CanExecute(null)); // nothing loaded, so nothing selected
        Assert.False(vm.RetryFailedCommand.CanExecute(null)); // and nothing has failed
    }

    [Fact]
    public async Task SyncCommand_RequiresTargetFolder_AndASelectedRepo()
    {
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"));
        Assert.True(vm.SyncCommand.CanExecute(null));

        vm.Repos[0].IsSelected = false;
        Assert.False(vm.SyncCommand.CanExecute(null));

        vm.Repos[0].IsSelected = true;
        vm.TargetFolder = "  ";
        Assert.False(vm.SyncCommand.CanExecute(null));
    }

    // ---------------------------------------------------------------- load flow

    [Fact]
    public async Task LoadRepos_FillsTable_AllSelected_AllQueued()
    {
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"), Repo("charlie"));

        Assert.Equal(3, vm.Repos.Count);
        Assert.Equal(3, vm.TotalCount);
        Assert.Equal(0, vm.CompletedCount);
        Assert.Equal("3 repositories loaded.", vm.StatusText);
        Assert.False(vm.IsLoadingRepos);
        Assert.True(vm.AllSelected);
        Assert.All(vm.Repos, r => Assert.True(r.IsSelected));
        Assert.All(vm.Repos, r => Assert.Equal(SyncStatus.Queued, r.Status));
        Assert.Empty(_git.CloneCalls); // loading must not touch git
        Assert.Empty(_git.PullCalls);
    }

    [Fact]
    public async Task LoadRepos_ListerFailure_SurfacesMessage_AndResetsLoading()
    {
        _lister.ExceptionToThrow = new InvalidOperationException("org not found");
        var vm = CreateViewModel();
        vm.Organization = "acme";
        vm.Token = "token-1234567890";
        await WaitUntilAsync(() => vm.StatusText.Length > 0, "org lookup to settle");

        await vm.LoadReposCommand.ExecuteAsync(null);

        Assert.Equal("org not found", vm.StatusText);
        Assert.Empty(vm.Repos);
        Assert.False(vm.IsLoadingRepos);
        Assert.True(vm.LoadReposCommand.CanExecute(null));
        Assert.False(vm.SyncCommand.CanExecute(null));
    }

    // ---------------------------------------------------------------- connect card state

    [Fact]
    public async Task HasLoadedRepos_TurnsOnAfterTheFirstSuccessfulLoad_Only()
    {
        _lister.ExceptionToThrow = new InvalidOperationException("org not found");
        var vm = CreateViewModel();
        vm.Organization = "acme";
        vm.Token = "token-1234567890";
        await WaitUntilAsync(() => vm.StatusText.Length > 0, "org lookup to settle");
        Assert.False(vm.HasLoadedRepos);

        await vm.LoadReposCommand.ExecuteAsync(null);
        Assert.False(vm.HasLoadedRepos); // a failed load keeps the connect card

        _lister.ExceptionToThrow = null;
        _lister.Repositories = [Repo("alpha")];
        await vm.LoadReposCommand.ExecuteAsync(null);
        Assert.True(vm.HasLoadedRepos);
    }

    [Fact]
    public async Task CanEditInputs_LocksDuringLoad_AndDuringSync()
    {
        var lister = new GatedLister();
        var vm = new WorkspaceViewModel(lister, _git, _orgs,
            handler => new SyncProgress(handler),
            TimeSpan.FromMilliseconds(1),
            new NullActivityLog());
        vm.Organization = "acme";
        vm.Token = "token-1234567890";
        await WaitUntilAsync(() => vm.StatusText.Length > 0, "org lookup to settle");
        vm.TargetFolder = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));
        vm.MaxConcurrencyValue = 1;
        Assert.True(vm.CanEditInputs);

        Task load = vm.LoadReposCommand.ExecuteAsync(null);
        Assert.False(vm.CanEditInputs); // locked while listing
        lister.Gate.SetResult([Repo("alpha")]);
        await load;
        Assert.True(vm.CanEditInputs);

        var gate = new TaskCompletionSource();
        int started = 0;
        _git.CloneHandler = async (url, path, token, onProgress, ct) =>
        {
            Interlocked.Increment(ref started);
            // CancellationToken.None: the gate must not be cancelable — the timeout is the safety valve.
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        };
        Task run = vm.SyncCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => started > 0, "the run to start");
        Assert.False(vm.CanEditInputs); // locked while syncing

        gate.SetResult();
        await run;
        Assert.True(vm.CanEditInputs);

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    // ---------------------------------------------------------------- sync flow

    [Fact]
    public async Task LoadThenSync_SyncsOnlySelectedRepos()
    {
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"), Repo("charlie"));
        var byName = vm.Repos.ToDictionary(r => r.Name);
        byName["bravo"].IsSelected = false;

        await vm.SyncCommand.ExecuteAsync(null);

        Assert.Equal(["alpha", "charlie"], _git.ClonedRepoNames);
        Assert.DoesNotContain(_git.ValidityChecks, p => Path.GetFileName(p) == "bravo");
        Assert.Empty(_git.PullCalls);
        Assert.Equal(SyncStatus.Done, byName["alpha"].Status);
        Assert.Equal(SyncStatus.Queued, byName["bravo"].Status); // untouched by the run
        Assert.Equal(SyncStatus.Done, byName["charlie"].Status);
        Assert.Equal(2, vm.CompletedCount);
        Assert.Equal(2, vm.TotalCount); // run-scoped (#22): the run set was 2, not the table's 3
        Assert.Contains("2 cloned", vm.StatusText);
        Assert.True(vm.HasCompletedRun);
        Assert.False(vm.IsRunning);

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    [Fact]
    public async Task Sync_TracksPulls_AndSummarizesFailures()
    {
        _git.IsValidRepositoryHandler = path => Path.GetFileName(path) == "alpha"; // alpha pulls
        _git.CloneHandler = (url, path, token, onProgress, ct) =>
            Path.GetFileName(path) == "charlie"
                ? Task.FromException(new InvalidOperationException("boom"))
                : Task.CompletedTask;

        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"), Repo("charlie"));

        await vm.SyncCommand.ExecuteAsync(null);

        var byName = vm.Repos.ToDictionary(r => r.Name);
        Assert.Equal(["alpha"], _git.PulledRepoNames);
        Assert.Equal(SyncStatus.Done, byName["alpha"].Status);
        Assert.Equal(SyncStatus.Done, byName["bravo"].Status);
        Assert.Equal(SyncStatus.Failed, byName["charlie"].Status);
        Assert.Contains("boom", byName["charlie"].Error);
        Assert.Equal(3, vm.CompletedCount);
        Assert.Contains("1 updated", vm.StatusText);
        Assert.Contains("1 failed", vm.StatusText);

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    [Fact]
    public async Task Sync_RepoFailure_RaisesAssistiveAnnouncement()
    {
        _git.CloneHandler = (url, path, token, onProgress, ct) =>
            Path.GetFileName(path) == "bravo"
                ? Task.FromException(new InvalidOperationException("boom"))
                : Task.CompletedTask;

        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"));
        var announcements = new List<string>();
        vm.AnnouncementRequested += announcements.Add;

        await vm.SyncCommand.ExecuteAsync(null);

        var announcement = Assert.Single(announcements);
        Assert.Contains("bravo", announcement);
        Assert.Contains("failed", announcement);
        Assert.Contains("boom", announcement);

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    [Fact]
    public async Task Sync_AllSucceed_RaisesNoAnnouncements()
    {
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"));
        var announcements = new List<string>();
        vm.AnnouncementRequested += announcements.Add;

        await vm.SyncCommand.ExecuteAsync(null);

        Assert.Empty(announcements);

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    [Fact]
    public async Task Sync_CancelCommand_CancelsRun_AndReportsCanceled()
    {
        var gate = new TaskCompletionSource();
        int started = 0;
        _git.CloneHandler = async (url, path, token, onProgress, ct) =>
        {
            Interlocked.Increment(ref started);
            // CancellationToken.None: the gate must not be cancelable — the timeout is the safety valve.
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            ct.ThrowIfCancellationRequested();
        };

        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"));

        Task run = vm.SyncCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => started > 0, "first clone to start");

        Assert.True(vm.IsRunning);
        Assert.False(vm.SyncCommand.CanExecute(null)); // all three commands lock while running
        Assert.False(vm.LoadReposCommand.CanExecute(null));
        Assert.False(vm.RetryFailedCommand.CanExecute(null));

        vm.SyncCancelCommand.Execute(null);
        gate.SetResult();
        await run;

        Assert.StartsWith("Canceled", vm.StatusText);
        Assert.False(vm.IsRunning);
        Assert.True(vm.HasCompletedRun);
        Assert.All(vm.Repos, r => Assert.True(
            r.Status is SyncStatus.Canceled or SyncStatus.Done or SyncStatus.Failed,
            $"{r.Name} left in non-terminal state {r.Status}"));

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    // ---------------------------------------------------------------- run-scoped progress

    [Fact]
    public async Task Sync_SubsetRun_ScopesProgressToTheRunSet()
    {
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"), Repo("charlie"));
        var byName = vm.Repos.ToDictionary(r => r.Name);
        byName["bravo"].IsSelected = false;

        await vm.SyncCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.TotalCount); // the two selected rows, not the table's three (#22)
        Assert.Equal(2, vm.CompletedCount);
        Assert.Equal(SyncStatus.Queued, byName["bravo"].Status); // unselected row untouched

        // The run size sticks until the next load resets the counters to the table.
        await vm.LoadReposCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.TotalCount);
        Assert.Equal(0, vm.CompletedCount);

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    // ---------------------------------------------------------------- active strip

    [Fact]
    public async Task ActiveRepos_TracksInFlightRows_AndEmptiesWhenTheRunEnds()
    {
        var gateAlpha = new TaskCompletionSource();
        var gateBravo = new TaskCompletionSource();
        _git.CloneHandler = async (url, path, token, onProgress, ct) =>
        {
            Task gate = Path.GetFileName(path) == "alpha" ? gateAlpha.Task : gateBravo.Task;
            // CancellationToken.None: the gates must not be cancelable — the timeout is the safety valve.
            await gate.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        };
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo")); // concurrency 1
        var byName = vm.Repos.ToDictionary(r => r.Name);
        Assert.Empty(vm.ActiveRepos);

        Task run = vm.SyncCommand.ExecuteAsync(null);
        await WaitUntilAsync(
            () => byName["alpha"].Status == SyncStatus.Cloning && vm.ActiveRepos.Count == 1,
            "alpha to enter the active strip");
        Assert.Equal("alpha", Assert.Single(vm.ActiveRepos).Name); // bounded by parallelism (1)

        gateAlpha.SetResult();
        await WaitUntilAsync(
            () => byName["bravo"].Status == SyncStatus.Cloning && vm.ActiveRepos.Count == 1,
            "alpha to leave and bravo to enter the active strip");
        Assert.Equal("bravo", Assert.Single(vm.ActiveRepos).Name); // alpha was removed on Done

        gateBravo.SetResult();
        await run;

        Assert.Empty(vm.ActiveRepos); // cleared when the run ends
        Assert.All(vm.Repos, r => Assert.Equal(SyncStatus.Done, r.Status));

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    // ---------------------------------------------------------------- retry failed

    [Fact]
    public async Task RetryFailed_ReRunsOnlyTheFailedRepos()
    {
        _git.CloneHandler = (url, path, token, onProgress, ct) =>
            Path.GetFileName(path) == "bravo"
                ? Task.FromException(new InvalidOperationException("boom"))
                : Task.CompletedTask;

        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"), Repo("charlie"));
        await vm.SyncCommand.ExecuteAsync(null);

        var byName = vm.Repos.ToDictionary(r => r.Name);
        Assert.Equal(SyncStatus.Failed, byName["bravo"].Status);
        Assert.True(vm.RetryFailedCommand.CanExecute(null));
        int clonesBeforeRetry = _git.CloneCalls.Count;

        _git.CloneHandler = (_, _, _, _, _) => Task.CompletedTask; // transient failure is gone
        await vm.RetryFailedCommand.ExecuteAsync(null);

        Assert.Equal(["bravo"], _git.ClonedRepoNames.Skip(clonesBeforeRetry));
        Assert.Equal(SyncStatus.Done, byName["bravo"].Status);
        Assert.Null(byName["bravo"].Error);
        Assert.Equal(SyncStatus.Done, byName["alpha"].Status); // untouched by the retry
        Assert.True(byName["bravo"].IsSelected); // retry selected exactly the failures
        Assert.False(byName["alpha"].IsSelected);
        Assert.False(byName["charlie"].IsSelected);
        Assert.Equal(1, vm.TotalCount); // run-scoped (#22): the retry run was just bravo
        Assert.Equal(1, vm.CompletedCount);
        Assert.False(vm.RetryFailedCommand.CanExecute(null)); // nothing failed anymore

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    // ---------------------------------------------------------------- run results

    [Fact]
    public async Task Sync_AllSucceed_ReportsASuccessResult()
    {
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"));
        Assert.False(vm.ResultOpen); // nothing to show before the first run
        Assert.Equal(RunResultKind.None, vm.ResultKind);

        await vm.SyncCommand.ExecuteAsync(null);

        Assert.True(vm.ResultOpen);
        Assert.Equal(RunResultKind.Success, vm.ResultKind);
        Assert.Contains("2 cloned", vm.ResultMessage);

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    [Fact]
    public async Task Sync_WithAFailure_ReportsAPartialFailureResult()
    {
        _git.CloneHandler = (url, path, token, onProgress, ct) =>
            Path.GetFileName(path) == "bravo"
                ? Task.FromException(new InvalidOperationException("boom"))
                : Task.CompletedTask;
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"));

        await vm.SyncCommand.ExecuteAsync(null);

        Assert.True(vm.ResultOpen);
        Assert.Equal(RunResultKind.PartialFailure, vm.ResultKind);
        Assert.Contains("1 failed", vm.ResultMessage);

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    [Fact]
    public async Task Sync_Canceled_ReportsACanceledResult()
    {
        var gate = new TaskCompletionSource();
        int started = 0;
        _git.CloneHandler = async (url, path, token, onProgress, ct) =>
        {
            Interlocked.Increment(ref started);
            // CancellationToken.None: the gate must not be cancelable — the timeout is the safety valve.
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            ct.ThrowIfCancellationRequested();
        };
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"));

        Task run = vm.SyncCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => started > 0, "first clone to start");
        vm.SyncCancelCommand.Execute(null);
        gate.SetResult();
        await run;

        Assert.True(vm.ResultOpen);
        Assert.Equal(RunResultKind.Canceled, vm.ResultKind);
        Assert.StartsWith("Canceled", vm.ResultMessage);

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    [Fact]
    public async Task Sync_RunLevelError_ReportsAnErrorResult()
    {
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"));
        // A file squats on the target root, so the engine's CreateDirectory throws
        // before any repository is processed.
        Directory.CreateDirectory(Path.GetDirectoryName(vm.TargetFolder)!);
        File.WriteAllText(vm.TargetFolder, "not a directory");
        try
        {
            await vm.SyncCommand.ExecuteAsync(null);

            Assert.True(vm.ResultOpen);
            Assert.Equal(RunResultKind.Error, vm.ResultKind);
            Assert.Equal(vm.StatusText, vm.ResultMessage); // the error line, verbatim
            Assert.NotEmpty(vm.ResultMessage);
            Assert.False(vm.IsRunning);
        }
        finally
        {
            File.Delete(vm.TargetFolder);
        }
    }

    [Fact]
    public async Task Sync_NextRun_ResetsTheResultSurface_WhileRunning()
    {
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"));
        await vm.SyncCommand.ExecuteAsync(null);
        Assert.True(vm.ResultOpen);

        var gate = new TaskCompletionSource();
        int started = 0;
        _git.CloneHandler = async (url, path, token, onProgress, ct) =>
        {
            Interlocked.Increment(ref started);
            // CancellationToken.None: the gate must not be cancelable — the timeout is the safety valve.
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        };

        Task run = vm.SyncCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => started > 0, "second run to start");

        Assert.False(vm.ResultOpen); // the stale result is gone while the new run is live
        Assert.Equal(RunResultKind.None, vm.ResultKind);
        Assert.Equal("", vm.ResultMessage);

        gate.SetResult();
        await run;

        Assert.True(vm.ResultOpen);
        Assert.Equal(RunResultKind.Success, vm.ResultKind);

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    [Fact]
    public async Task LoadRepos_ClearsTheResultSurface()
    {
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"));
        await vm.SyncCommand.ExecuteAsync(null);
        Assert.True(vm.ResultOpen);

        await vm.LoadReposCommand.ExecuteAsync(null);

        Assert.False(vm.ResultOpen);
        Assert.Equal(RunResultKind.None, vm.ResultKind);
        Assert.Equal("", vm.ResultMessage);

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    // ---------------------------------------------------------------- path recovery

    private static readonly IReadOnlyList<InvalidPathInfo> SampleInvalidPaths =
    [
        new InvalidPathInfo("aux", "'aux' is a reserved Windows device name", "aux_"),
        new InvalidPathInfo("docs/bad:name.txt", "contains a character that is invalid on Windows", "bad_name.txt"),
    ];

    /// <summary>Clones of 'alpha' fail with <see cref="SampleInvalidPaths"/>; everything else succeeds.</summary>
    private void MakeAlphaFailWithInvalidPaths()
        => _git.CloneHandler = (url, path, token, onProgress, ct) =>
            Path.GetFileName(path) == "alpha"
                ? Task.FromException(new InvalidRepositoryPathsException(SampleInvalidPaths))
                : Task.CompletedTask;

    [Fact]
    public async Task Sync_InvalidPathFailure_FlowsPayloadToRow_AndEnablesResolve()
    {
        MakeAlphaFailWithInvalidPaths();
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"));

        await vm.SyncCommand.ExecuteAsync(null);

        var byName = vm.Repos.ToDictionary(r => r.Name);
        Assert.Equal(SyncStatus.Failed, byName["alpha"].Status);
        Assert.True(byName["alpha"].HasPathIssue);
        Assert.Equal(SampleInvalidPaths, byName["alpha"].InvalidPaths);
        Assert.Equal(SyncStatus.Done, byName["bravo"].Status);
        Assert.False(byName["bravo"].HasPathIssue);
        Assert.Null(byName["bravo"].InvalidPaths);

        Assert.True(vm.ResolvePathsCommand.CanExecute(byName["alpha"]));
        Assert.False(vm.ResolvePathsCommand.CanExecute(byName["bravo"])); // no path issue
        Assert.False(vm.ResolvePathsCommand.CanExecute(null));

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    [Fact]
    public async Task ResolvePaths_AppliesRecovery_AndMarksRowDone()
    {
        MakeAlphaFailWithInvalidPaths();
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"));
        await vm.SyncCommand.ExecuteAsync(null);
        RepoItemViewModel row = vm.Repos[0];

        var recovery = new PathRecovery(
            new Dictionary<string, string> { ["aux"] = "aux_" },
            new HashSet<string> { "docs/bad:name.txt" });
        var asked = new List<RepoItemViewModel>();
        vm.RecoveryInteraction = item =>
        {
            asked.Add(item);
            return Task.FromResult<PathRecovery?>(recovery);
        };

        await vm.ResolvePathsCommand.ExecuteAsync(row);

        Assert.Equal([row], asked);
        ApplyRecoveryCall call = Assert.Single(_git.ApplyRecoveryCalls);
        Assert.Equal(Path.Combine(vm.EffectiveTargetRoot, "alpha"), call.LocalPath);
        Assert.Same(recovery, call.Recovery);

        Assert.Equal(SyncStatus.Done, row.Status);
        Assert.Null(row.Error);
        Assert.Null(row.InvalidPaths);
        Assert.False(row.HasPathIssue);
        Assert.False(vm.ResolvePathsCommand.CanExecute(row)); // nothing left to resolve
        Assert.False(vm.RetryFailedCommand.CanExecute(null)); // nothing failed anymore

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    [Fact]
    public async Task ResolvePaths_ShowsPulling_WhileTheRecoveryIsInFlight()
    {
        MakeAlphaFailWithInvalidPaths();
        var gate = new TaskCompletionSource();
        _git.ApplyRecoveryHandler = async (_, _, _) =>
            // CancellationToken.None: the gate must not be cancelable — the timeout is the safety valve.
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"));
        await vm.SyncCommand.ExecuteAsync(null);
        RepoItemViewModel row = vm.Repos[0];
        vm.RecoveryInteraction = _ => Task.FromResult<PathRecovery?>(
            new PathRecovery(new Dictionary<string, string>(), new HashSet<string>()));

        Task resolve = vm.ResolvePathsCommand.ExecuteAsync(row);
        await WaitUntilAsync(() => row.Status == SyncStatus.Pulling, "the in-flight recovery to show");

        Assert.True(row.ShowProgress); // the row's indeterminate bar is visible
        Assert.True(row.IsIndeterminate);

        gate.SetResult();
        await resolve;

        Assert.Equal(SyncStatus.Done, row.Status);

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    [Fact]
    public async Task ResolvePaths_InteractionReturnsNull_LeavesRowFailed()
    {
        MakeAlphaFailWithInvalidPaths();
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"));
        await vm.SyncCommand.ExecuteAsync(null);
        RepoItemViewModel row = vm.Repos[0];
        vm.RecoveryInteraction = _ => Task.FromResult<PathRecovery?>(null);

        await vm.ResolvePathsCommand.ExecuteAsync(row);

        Assert.Empty(_git.ApplyRecoveryCalls); // canceled: nothing applied
        Assert.Equal(SyncStatus.Failed, row.Status);
        Assert.True(row.HasPathIssue); // payload kept for another attempt
        Assert.True(vm.ResolvePathsCommand.CanExecute(row));

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    [Fact]
    public async Task ResolvePaths_ApplyStillInvalid_KeepsRowFailed_WithFreshPayload()
    {
        MakeAlphaFailWithInvalidPaths();
        var stillInvalid = new List<InvalidPathInfo>
        {
            new("aux_", "differs only by case from 'AUX_'", null),
        };
        _git.ApplyRecoveryHandler = (_, _, _) =>
            Task.FromException(new InvalidRepositoryPathsException(stillInvalid));
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"));
        await vm.SyncCommand.ExecuteAsync(null);
        RepoItemViewModel row = vm.Repos[0];
        vm.RecoveryInteraction = _ => Task.FromResult<PathRecovery?>(
            new PathRecovery(new Dictionary<string, string>(), new HashSet<string>()));

        await vm.ResolvePathsCommand.ExecuteAsync(row);

        Assert.Single(_git.ApplyRecoveryCalls);
        Assert.Equal(SyncStatus.Failed, row.Status);
        Assert.Contains("aux_", row.Error);
        Assert.Equal(stillInvalid, row.InvalidPaths); // the fresh list, ready for another round
        Assert.True(vm.ResolvePathsCommand.CanExecute(row));

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    [Fact]
    public async Task ResolvePaths_ApplyFailsForAnotherReason_ClearsPayload()
    {
        MakeAlphaFailWithInvalidPaths();
        _git.ApplyRecoveryHandler = (_, _, _) =>
            Task.FromException(new IOException("disk full"));
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"));
        await vm.SyncCommand.ExecuteAsync(null);
        RepoItemViewModel row = vm.Repos[0];
        vm.RecoveryInteraction = _ => Task.FromResult<PathRecovery?>(
            new PathRecovery(new Dictionary<string, string>(), new HashSet<string>()));

        await vm.ResolvePathsCommand.ExecuteAsync(row);

        Assert.Equal(SyncStatus.Failed, row.Status);
        Assert.Equal("disk full", row.Error);
        Assert.False(row.HasPathIssue); // renaming again would not help

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    [Fact]
    public async Task ResolvePaths_DisabledWhileARunIsInFlight()
    {
        MakeAlphaFailWithInvalidPaths();
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"));
        await vm.SyncCommand.ExecuteAsync(null);
        var byName = vm.Repos.ToDictionary(r => r.Name);
        RepoItemViewModel alpha = byName["alpha"];
        Assert.True(vm.ResolvePathsCommand.CanExecute(alpha));

        // Second run touches only bravo, so alpha keeps its payload throughout.
        alpha.IsSelected = false;
        var gate = new TaskCompletionSource();
        int started = 0;
        _git.CloneHandler = async (url, path, token, onProgress, ct) =>
        {
            Interlocked.Increment(ref started);
            // CancellationToken.None: the gate must not be cancelable — the timeout is the safety valve.
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        };

        Task run = vm.SyncCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => started > 0, "second run to start");
        Assert.True(alpha.HasPathIssue);
        Assert.False(vm.ResolvePathsCommand.CanExecute(alpha)); // locked while running

        gate.SetResult();
        await run;
        Assert.True(vm.ResolvePathsCommand.CanExecute(alpha)); // unlocked again

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    [Fact]
    public async Task Sync_ReRun_ClearsAStalePathPayload()
    {
        MakeAlphaFailWithInvalidPaths();
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"));
        await vm.SyncCommand.ExecuteAsync(null);
        RepoItemViewModel row = vm.Repos[0];
        Assert.True(row.HasPathIssue);

        _git.CloneHandler = (_, _, _, _, _) => Task.CompletedTask; // upstream fixed the paths
        await vm.SyncCommand.ExecuteAsync(null);

        Assert.Equal(SyncStatus.Done, row.Status);
        Assert.Null(row.InvalidPaths);
        Assert.False(row.HasPathIssue);
        Assert.False(vm.ResolvePathsCommand.CanExecute(row));

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    // ---------------------------------------------------------------- selection

    [Fact]
    public async Task AllSelected_TogglesEveryRow_AndFollowsItemChanges()
    {
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"), Repo("charlie"));
        Assert.True(vm.AllSelected);

        vm.AllSelected = false;
        Assert.All(vm.Repos, r => Assert.False(r.IsSelected));
        Assert.False(vm.SyncCommand.CanExecute(null)); // nothing selected

        vm.AllSelected = true;
        Assert.All(vm.Repos, r => Assert.True(r.IsSelected));
        Assert.True(vm.SyncCommand.CanExecute(null));

        vm.Repos[1].IsSelected = false;
        Assert.False(vm.AllSelected);
        Assert.True(vm.Repos[0].IsSelected); // header recompute must not cascade to other rows
        Assert.True(vm.Repos[2].IsSelected);

        vm.Repos[1].IsSelected = true;
        Assert.True(vm.AllSelected);
    }

    [Fact]
    public async Task SelectedCount_AndSyncButtonLabel_FollowSelectionChanges()
    {
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"), Repo("charlie"));
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.Equal(3, vm.SelectedCount);
        Assert.Equal("Sync 3 repos", vm.SyncButtonLabel);

        vm.Repos[0].IsSelected = false;
        vm.Repos[1].IsSelected = false;
        Assert.Equal(1, vm.SelectedCount);
        Assert.Equal("Sync 1 repo", vm.SyncButtonLabel); // singular
        Assert.Contains(nameof(WorkspaceViewModel.SyncButtonLabel), raised);

        vm.Repos[2].IsSelected = false;
        Assert.Equal(0, vm.SelectedCount);
        Assert.Equal("Sync 0 repos", vm.SyncButtonLabel);

        vm.AllSelected = true; // header toggle recomputes too
        Assert.Equal(3, vm.SelectedCount);
        Assert.Equal("Sync 3 repos", vm.SyncButtonLabel);
    }

    // ---------------------------------------------------------------- sorting

    [Fact]
    public async Task Sort_ByName_TogglesDirection_AndSwitchingColumnResetsIt()
    {
        var vm = await CreateLoadedViewModelAsync(Repo("bravo"), Repo("alpha"), Repo("charlie"));

        vm.SortCommand.Execute("Name");
        Assert.Equal(["alpha", "bravo", "charlie"], vm.Repos.Select(r => r.Name));
        Assert.Equal("Name", vm.SortColumn);
        Assert.False(vm.SortDescending);

        vm.SortCommand.Execute("Name");
        Assert.Equal(["charlie", "bravo", "alpha"], vm.Repos.Select(r => r.Name));
        Assert.True(vm.SortDescending);

        vm.SortCommand.Execute("Status");
        Assert.Equal("Status", vm.SortColumn);
        Assert.False(vm.SortDescending); // direction resets on a new column
    }

    [Fact]
    public async Task Sort_ByStatus_OrdersByLifecycle_AndIsStable()
    {
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"), Repo("charlie"));
        var byName = vm.Repos.ToDictionary(r => r.Name);
        byName["alpha"].Status = SyncStatus.Done;
        byName["bravo"].Status = SyncStatus.Failed;
        byName["charlie"].Status = SyncStatus.Done;

        vm.SortCommand.Execute("Status");

        // Done sorts before Failed; the two Done rows keep their load order (stable sort).
        Assert.Equal(["alpha", "charlie", "bravo"], vm.Repos.Select(r => r.Name));
    }

    // ---------------------------------------------------------------- filtering

    [Fact]
    public async Task FilteredRepos_DefaultAll_MirrorsTheTableAfterLoad()
    {
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"));

        Assert.Equal(RepoFilter.All, vm.Filter);
        Assert.Equal(vm.Repos, vm.FilteredRepos);
    }

    [Fact]
    public async Task Filter_RebuildsFilteredRepos_PerMode()
    {
        var vm = await CreateLoadedViewModelAsync(
            Repo("alpha"), Repo("bravo"), Repo("charlie"), Repo("delta"));
        var byName = vm.Repos.ToDictionary(r => r.Name);
        byName["alpha"].Status = SyncStatus.Failed;
        byName["bravo"].Status = SyncStatus.Cloning;
        byName["charlie"].Status = SyncStatus.Pulling;
        // delta stays Queued

        vm.Filter = RepoFilter.Active;
        Assert.Equal(["bravo", "charlie"], vm.FilteredRepos.Select(r => r.Name));

        vm.Filter = RepoFilter.Failed;
        Assert.Equal(["alpha"], vm.FilteredRepos.Select(r => r.Name));

        vm.Filter = RepoFilter.Pending;
        Assert.Equal(["delta"], vm.FilteredRepos.Select(r => r.Name));

        vm.Filter = RepoFilter.All;
        Assert.Equal(["alpha", "bravo", "charlie", "delta"], vm.FilteredRepos.Select(r => r.Name));
    }

    [Fact]
    public async Task Filter_WithNoMatchingRows_LeavesFilteredReposEmpty()
    {
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"));

        vm.Filter = RepoFilter.Failed; // nothing has failed

        Assert.Empty(vm.FilteredRepos);
        Assert.NotEmpty(vm.Repos); // the empty-filter hint case: rows exist, none match
    }

    [Fact]
    public async Task NameFilter_CaseInsensitiveSubstring_ComposesWithStatusFilter()
    {
        var vm = await CreateLoadedViewModelAsync(
            Repo("platform-api"), Repo("platform-web"), Repo("tools"));
        vm.Repos.First(r => r.Name == "platform-api").Status = SyncStatus.Failed;

        vm.NameFilter = "PLATFORM";
        Assert.Equal(["platform-api", "platform-web"], vm.FilteredRepos.Select(r => r.Name));

        vm.Filter = RepoFilter.Failed; // AND-composes with the name filter
        Assert.Equal(["platform-api"], vm.FilteredRepos.Select(r => r.Name));

        vm.NameFilter = "";
        vm.Filter = RepoFilter.All;
        Assert.Equal(3, vm.FilteredRepos.Count);
    }

    [Fact]
    public async Task NameFilter_TrimsBeforeMatching()
    {
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"));

        vm.NameFilter = "  alpha  ";

        Assert.Equal(["alpha"], vm.FilteredRepos.Select(r => r.Name));
    }

    [Fact]
    public async Task BranchFilter_MatchesTheDisplayedBranchText()
    {
        var vm = await CreateLoadedViewModelAsync(
            Repo("alpha", branch: "main"),
            Repo("bravo", branch: "develop"),
            Repo("empty", branch: null)); // empty repository: BranchText is ""

        vm.BranchFilter = "dev";

        Assert.Equal(["bravo"], vm.FilteredRepos.Select(r => r.Name));
    }

    [Fact]
    public async Task ArchivedFilter_TriState_AllArchivedUnarchived()
    {
        var vm = await CreateLoadedViewModelAsync(
            Repo("alpha", archived: true), Repo("bravo"));

        vm.ArchivedFilter = true;
        Assert.Equal(["alpha"], vm.FilteredRepos.Select(r => r.Name));

        vm.ArchivedFilter = false;
        Assert.Equal(["bravo"], vm.FilteredRepos.Select(r => r.Name));

        vm.ArchivedFilter = null;
        Assert.Equal(2, vm.FilteredRepos.Count);
    }

    [Fact]
    public async Task ColumnFilters_NeverTouchSelection_HiddenRowsStillSync()
    {
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"));
        Assert.True(vm.Repos.All(r => r.IsSelected)); // load selects everything

        vm.NameFilter = "alpha"; // bravo is hidden but stays selected

        Assert.Equal(2, vm.SelectedCount);
        Assert.Contains("2", vm.SyncButtonLabel);
    }

    [Fact]
    public async Task FilteredRepos_FollowsTerminalTransitions_AcrossARun()
    {
        _git.CloneHandler = (url, path, token, onProgress, ct) =>
            Path.GetFileName(path) == "bravo"
                ? Task.FromException(new InvalidOperationException("boom"))
                : Task.CompletedTask;
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"));
        vm.Filter = RepoFilter.Failed;
        Assert.Empty(vm.FilteredRepos);

        await vm.SyncCommand.ExecuteAsync(null);

        Assert.Equal(["bravo"], vm.FilteredRepos.Select(r => r.Name));

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    [Fact]
    public async Task FilteredRepos_PreservesTheTableSortOrder()
    {
        var vm = await CreateLoadedViewModelAsync(Repo("bravo"), Repo("alpha"), Repo("charlie"));

        vm.SortCommand.Execute("Name");

        Assert.Equal(["alpha", "bravo", "charlie"], vm.FilteredRepos.Select(r => r.Name));
    }

    // ---------------------------------------------------------------- target preview

    [Fact]
    public void TargetPreview_ReflectsOrgSubfolder_AndRaisesChange()
    {
        var vm = CreateViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Organization = "acme";
        vm.TargetFolder = @"C:\src";

        Assert.Equal(@"C:\src", vm.EffectiveTargetRoot);
        Assert.Equal(@"C:\src\my-repo", vm.TargetPreview);

        raised.Clear();
        vm.CreateOrgSubfolder = true;

        Assert.Equal(@"C:\src\acme", vm.EffectiveTargetRoot);
        Assert.Equal(@"C:\src\acme\my-repo", vm.TargetPreview);
        Assert.Contains(nameof(WorkspaceViewModel.EffectiveTargetRoot), raised);
        Assert.Contains(nameof(WorkspaceViewModel.TargetPreview), raised);
    }

    [Fact]
    public async Task TargetPreview_UsesFirstLoadedRepoName()
    {
        var vm = await CreateLoadedViewModelAsync(Repo("zulu"), Repo("alpha"));

        Assert.Equal(Path.Combine(vm.TargetFolder, "zulu"), vm.TargetPreview);

        vm.CreateOrgSubfolder = true;
        Assert.Equal(Path.Combine(vm.TargetFolder, "acme", "zulu"), vm.TargetPreview);
    }

    // ---------------------------------------------------------------- org lookup

    [Fact]
    public async Task TokenChange_PopulatesOrganizations_AfterDebounce()
    {
        _orgs.Handler = (_, _) => Task.FromResult<IReadOnlyList<string>>(["KofTwentyTwo", "acme"]);
        var vm = CreateViewModel();

        vm.Token = "token-1234567890";

        await WaitUntilAsync(() => vm.Organizations.Count == 2, "organizations to load");
        Assert.Equal(["KofTwentyTwo", "acme"], vm.Organizations);
        Assert.Contains("Found 2", vm.StatusText);
        Assert.False(vm.IsLoadingOrgs);
    }

    [Fact]
    public async Task TokenChange_OnlyPersonalLoginVisible_ExplainsAndAllowsManualEntry()
    {
        // The production lister always includes the token's own account, so a
        // single-entry answer means no organizations were visible.
        _orgs.Handler = (_, _) => Task.FromResult<IReadOnlyList<string>>(["KofTwentyTwo"]);
        var vm = CreateViewModel();

        vm.Token = "token-1234567890";

        await WaitUntilAsync(() => vm.StatusText.Length > 0, "status text");
        Assert.Equal(
            "Only your personal account is visible — add read:org (classic) for organizations, "
            + "or type an org name manually.",
            vm.StatusText);
        Assert.Equal(["KofTwentyTwo"], vm.Organizations);
    }

    [Fact]
    public async Task TokenChange_ToShortTokenDuringLookup_ClearsTheLoadingFlag()
    {
        // The first lookup blocks inside the lister; editing the token to something
        // implausibly short cancels it and returns early — the early return owns the
        // flag now, so it must not leave the spinner stuck on.
        var gate = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _orgs.Handler = (_, _) => gate.Task;
        var vm = CreateViewModel();

        vm.Token = "token-1234567890";
        await WaitUntilAsync(() => vm.IsLoadingOrgs, "the first lookup to start");

        vm.Token = "short";

        Assert.False(vm.IsLoadingOrgs);
        Assert.Empty(vm.Organizations);
        gate.SetResult(["acme"]); // release the abandoned lookup; it was canceled and is ignored
    }

    [Fact]
    public async Task TokenChange_LookupFailure_SurfacesMessage_WithoutThrowing()
    {
        _orgs.Handler = (_, _) => Task.FromException<IReadOnlyList<string>>(
            new InvalidOperationException("GitHub rejected the token (401). Check the PAT."));
        var vm = CreateViewModel();

        vm.Token = "token-1234567890";

        await WaitUntilAsync(() => vm.StatusText.Contains("401"), "error to surface");
        Assert.False(vm.IsLoadingOrgs);
    }

    [Fact]
    public async Task TokenChange_TooShort_ClearsListWithoutLookup()
    {
        _orgs.Handler = (_, _) => Task.FromResult<IReadOnlyList<string>>(["acme"]);
        var vm = CreateViewModel();
        vm.Token = "token-1234567890";
        await WaitUntilAsync(() => vm.Organizations.Count == 1, "initial load");

        vm.Token = "short";

        Assert.Empty(vm.Organizations);
        Assert.Equal(1, _orgs.Calls); // no second lookup for an implausible token
    }

    [Fact]
    public async Task TokenChange_RapidEdits_OnlyNewestLookupLands()
    {
        _orgs.Handler = (token, _) => Task.FromResult<IReadOnlyList<string>>([token[^4..]]);
        var vm = CreateViewModel(debounce: TimeSpan.FromMilliseconds(120));

        vm.Token = "token-1234567890-AAAA";
        vm.Token = "token-1234567890-BBBB"; // supersedes within the debounce window

        await WaitUntilAsync(() => vm.Organizations.Count == 1, "debounced load");
        Assert.Equal("BBBB", vm.Organizations[0]);
        Assert.Equal(1, _orgs.Calls);
    }

    // ---------------------------------------------------------------- account workspaces

    /// <summary>An account profile whose repositories land under <paramref name="targetRoot"/>.</summary>
    private static Account MakeAccount(string targetRoot) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Work",
        Organization = "acme",
        TargetRoot = targetRoot,
        MaxConcurrency = 1, // serialize progress: the sync fake reports inline across threads
    };

    [Fact]
    public async Task OrgRefresh_AccountSeededOrganization_SurvivesAndResyncsTheView()
    {
        // Regression: the view's editable ComboBox resets its Text when its
        // ItemsSource is mutated, blanking the org an account workspace seeded in
        // the constructor. The VM must keep the value across the refresh AND raise
        // Organization again after the list mutation so the view resyncs its Text.
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Name = "Kof",
            Organization = "KofTwentyTwo",
            TargetRoot = @"C:\repos\kof",
        };
        var vault = new InMemoryVault();
        vault.Store(account.Id, "vault-token-1234567890");
        _orgs.Handler = (_, _) =>
            Task.FromResult<IReadOnlyList<string>>(["KofTwentyTwo", "acme", "other"]);

        var vm = CreateViewModel(account: account, vault: vault);
        bool resyncAfterListArrived = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkspaceViewModel.Organization)
                && vm.Organizations.Count > 0)
            {
                resyncAfterListArrived = true;
            }
        };

        await WaitUntilAsync(() => vm.Organizations.Count == 3, "org list refresh");

        Assert.Equal("KofTwentyTwo", vm.Organization);
        Assert.True(resyncAfterListArrived, "Organization must be re-raised after the list mutation");
    }

    [Fact]
    public async Task AccountConstruction_SeedsWorkspace_AndLooksUpOrgsWithVaultToken()
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Name = "Work",
            Organization = "acme",
            TargetRoot = @"C:\repos\work",
            CreateOrgSubfolder = true,
            MaxConcurrency = 4,
        };
        var vault = new InMemoryVault();
        vault.Store(account.Id, "vault-token-1234567890");
        string? lookedUpToken = null;
        _orgs.Handler = (token, _) =>
        {
            lookedUpToken = token;
            return Task.FromResult<IReadOnlyList<string>>(["acme", "other"]);
        };

        var vm = CreateViewModel(account: account, vault: vault);

        Assert.Equal("acme", vm.Organization);
        Assert.Equal(@"C:\repos\work", vm.TargetFolder);
        Assert.True(vm.CreateOrgSubfolder);
        Assert.Equal(4, vm.MaxConcurrency);
        Assert.Equal("vault-token-1234567890", vm.Token);
        Assert.Equal(account.Id, vm.AccountId);
        Assert.Equal("Work", vm.DisplayName);

        await WaitUntilAsync(() => lookedUpToken is not null, "org lookup to fire");
        Assert.Equal("vault-token-1234567890", lookedUpToken);
    }

    [Fact]
    public void QuickSyncConstruction_KeepsDefaults_AndQuickSyncIdentity()
    {
        var vm = CreateViewModel();

        Assert.Null(vm.AccountId);
        Assert.Equal("Quick Sync", vm.DisplayName);
        Assert.Equal("", vm.Organization);
        Assert.Equal("", vm.Token);
        Assert.Equal("", vm.TargetFolder);
        Assert.False(vm.CreateOrgSubfolder);
        Assert.Equal(AppSettings.DefaultConcurrency, vm.MaxConcurrency);
        Assert.False(vm.HasFailedRepos);
    }

    [Fact]
    public async Task HasFailedRepos_TurnsOnWithAFailure_AndOffWhenRetrySucceeds()
    {
        _git.CloneHandler = (url, path, token, onProgress, ct) =>
            Path.GetFileName(path) == "bravo"
                ? Task.FromException(new InvalidOperationException("boom"))
                : Task.CompletedTask;
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"), Repo("bravo"));
        Assert.False(vm.HasFailedRepos);

        await vm.SyncCommand.ExecuteAsync(null);
        Assert.True(vm.HasFailedRepos);

        _git.CloneHandler = (_, _, _, _, _) => Task.CompletedTask; // transient failure is gone
        await vm.RetryFailedCommand.ExecuteAsync(null);
        Assert.False(vm.HasFailedRepos);

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    [Fact]
    public async Task Sync_AccountWorkspace_RecordsTheResultOnTheStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var vault = new InMemoryVault();
            var store = new AccountsStore(vault, Path.Combine(root, "store"), new NullActivityLog());
            var account = MakeAccount(Path.Combine(root, "repos"));
            store.Save(account, "token-1234567890"); // the vault feeds the VM its token
            _lister.Repositories = [Repo("alpha"), Repo("bravo")];

            var vm = CreateViewModel(account: account, vault: vault, store: store);
            await WaitUntilAsync(() => vm.StatusText.Length > 0, "org lookup to settle");
            await vm.LoadReposCommand.ExecuteAsync(null);

            await vm.SyncCommand.ExecuteAsync(null);

            Account recorded = Assert.Single(store.GetAll());
            Assert.NotNull(recorded.LastSyncUtc);
            Assert.NotNull(recorded.LastSyncSummary);
            Assert.StartsWith("Finished", recorded.LastSyncSummary);
            Assert.Contains("2 cloned", recorded.LastSyncSummary);
            Assert.Equal(vm.StatusText, recorded.LastSyncSummary);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Sync_QuickSync_DoesNotRecordOnTheStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var vault = new InMemoryVault();
            var store = new AccountsStore(vault, Path.Combine(root, "store"), new NullActivityLog());
            store.Save(MakeAccount(Path.Combine(root, "repos")), "token-1234567890");
            _lister.Repositories = [Repo("alpha")];

            // Quick Sync: the shared store exists, but no account backs this workspace.
            var vm = CreateViewModel(vault: vault, store: store);
            vm.Organization = "acme";
            vm.Token = "token-1234567890";
            await WaitUntilAsync(() => vm.StatusText.Length > 0, "org lookup to settle");
            vm.TargetFolder = Path.Combine(root, "quick");
            vm.MaxConcurrencyValue = 1;
            await vm.LoadReposCommand.ExecuteAsync(null);

            await vm.SyncCommand.ExecuteAsync(null);

            Assert.Contains("1 cloned", vm.StatusText);
            Account untouched = Assert.Single(store.GetAll());
            Assert.Null(untouched.LastSyncUtc);
            Assert.Null(untouched.LastSyncSummary);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Sync_RecordSyncResultFailure_StillCompletesTheRun()
    {
        string root = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));
        string storeDir = Path.Combine(root, "store");
        try
        {
            var vault = new InMemoryVault();
            var store = new AccountsStore(vault, storeDir, new NullActivityLog());
            var account = MakeAccount(Path.Combine(root, "repos"));
            store.Save(account, "token-1234567890");
            _lister.Repositories = [Repo("alpha")];

            var vm = CreateViewModel(account: account, vault: vault, store: store);
            await WaitUntilAsync(() => vm.StatusText.Length > 0, "org lookup to settle");
            await vm.LoadReposCommand.ExecuteAsync(null);

            // Break persistence: a file now squats on the store's directory path, so
            // RecordSyncResult's write throws. The finished sync must shrug that off.
            Directory.Delete(storeDir, recursive: true);
            File.WriteAllText(storeDir, "not a directory");

            await vm.SyncCommand.ExecuteAsync(null);

            Assert.Equal("Finished: 1 cloned, 0 updated, 0 failed, 0 canceled of 1.", vm.StatusText);
            Assert.True(vm.HasCompletedRun);
            Assert.False(vm.IsRunning);
            Assert.False(vm.HasFailedRepos);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    // ---------------------------------------------------------------- misc bindings

    [Theory]
    [InlineData(100.0, 64)]
    [InlineData(0.0, 1)]
    [InlineData(12.0, 12)]
    [InlineData(double.NaN, 8)]
    public void MaxConcurrencyValue_ClampsIntoValidRange(double input, int expected)
    {
        var vm = CreateViewModel();

        vm.MaxConcurrencyValue = input;

        Assert.Equal(expected, vm.MaxConcurrency);
    }

    [Fact]
    public void RepoItem_ExposesDescriptorFields_AndProgressShape()
    {
        var descriptor = new RepoDescriptor(
            "alpha", "https://example.test/acme/alpha.git", "develop", IsArchived: true);
        var item = new RepoItemViewModel(descriptor);

        Assert.Same(descriptor, item.Descriptor);
        Assert.Equal("alpha", item.Name);
        Assert.Equal("develop", item.BranchText);
        Assert.True(item.IsArchived);
        Assert.True(item.IsSelected); // rows start selected
        Assert.Equal("Queued", item.StatusText);
        Assert.False(item.ShowProgress);
        Assert.False(item.HasError);
        Assert.Equal(0, item.ProgressValue);

        item.Status = SyncStatus.Cloning;
        item.Percent = 0.42;
        Assert.Equal("Cloning 42%", item.StatusText);
        Assert.True(item.ShowProgress);
        Assert.False(item.IsIndeterminate);
        Assert.Equal(0.42, item.ProgressValue);

        item.Status = SyncStatus.Pulling;
        Assert.True(item.ShowProgress);
        Assert.True(item.IsIndeterminate); // pulls report no percentage

        item.Status = SyncStatus.Failed;
        item.Error = "boom";
        Assert.True(item.HasError);
        Assert.False(item.ShowProgress);
    }

    [Fact]
    public void RepoItem_EmptyRepository_HasEmptyBranchText()
    {
        var item = new RepoItemViewModel(Repo("empty", branch: null));

        Assert.Equal("", item.BranchText);
    }

    [Fact]
    public async Task ActiveFilter_FollowsNonTerminalTransitions_DuringARun()
    {
        var gate = new TaskCompletionSource();
        _git.CloneHandler = async (_, _, _, _, _) =>
            // CancellationToken.None: the gate must not be cancelable; the timeout is the safety valve.
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"));
        vm.Filter = RepoFilter.Active;
        Assert.Empty(vm.FilteredRepos); // nothing in flight yet

        Task run = vm.SyncCommand.ExecuteAsync(null);
        await WaitUntilAsync(
            () => vm.FilteredRepos.Count == 1 && vm.FilteredRepos[0].Name == "alpha",
            "the cloning row to enter the Active filter");

        gate.SetResult();
        await run;
        Assert.Empty(vm.FilteredRepos); // done rows leave the Active view
    }

    /// <summary>A lister whose calls each await their own gate, in order.</summary>
    private sealed class SequencedGatedLister : IRepositoryLister
    {
        public Queue<TaskCompletionSource<IReadOnlyList<RepoDescriptor>>> Gates { get; } = new();

        public Task<IReadOnlyList<RepoDescriptor>> ListOrganizationRepositoriesAsync(
            string organization, string token, CancellationToken cancellationToken = default)
            => Gates.Dequeue().Task;
    }

    [Fact]
    public async Task Sync_IsBlocked_WhileAReloadIsInFlight()
    {
        var lister = new SequencedGatedLister();
        var first = new TaskCompletionSource<IReadOnlyList<RepoDescriptor>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<IReadOnlyList<RepoDescriptor>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lister.Gates.Enqueue(first);
        lister.Gates.Enqueue(second);

        var vm = new WorkspaceViewModel(lister, _git, _orgs,
            handler => new SyncProgress(handler),
            TimeSpan.FromMilliseconds(1),
            new NullActivityLog());
        vm.Organization = "acme";
        vm.Token = "token-1234567890";
        await WaitUntilAsync(() => vm.StatusText.Length > 0, "org lookup to settle");
        vm.TargetFolder = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

        Task firstLoad = vm.LoadReposCommand.ExecuteAsync(null);
        first.SetResult([Repo("alpha")]);
        await firstLoad; // table populated, alpha selected
        Assert.True(vm.SyncCommand.CanExecute(null));

        Task reload = vm.LoadReposCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.IsLoadingRepos, "the reload to start");
        Assert.False(vm.SyncCommand.CanExecute(null));
        Assert.False(vm.RetryFailedCommand.CanExecute(null));

        second.SetResult([Repo("alpha")]);
        await reload;
        Assert.True(vm.SyncCommand.CanExecute(null));
    }

    [Fact]
    public async Task PathRecovery_LocksOutEveryOtherGitEntryPoint_WhileInFlight()
    {
        MakeAlphaFailWithInvalidPaths();
        var gate = new TaskCompletionSource();
        _git.ApplyRecoveryHandler = async (_, _, _) =>
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        var vm = await CreateLoadedViewModelAsync(Repo("alpha"));
        await vm.SyncCommand.ExecuteAsync(null);
        vm.RecoveryInteraction = _ => Task.FromResult<PathRecovery?>(
            new PathRecovery(new Dictionary<string, string>(), new HashSet<string>()));

        Task resolve = vm.ResolvePathsCommand.ExecuteAsync(vm.Repos[0]);
        await WaitUntilAsync(() => vm.IsResolvingPaths, "the recovery to start");
        Assert.False(vm.SyncCommand.CanExecute(null));
        Assert.False(vm.LoadReposCommand.CanExecute(null));
        Assert.False(vm.RetryFailedCommand.CanExecute(null));
        Assert.False(vm.CanEditInputs);

        gate.SetResult();
        await resolve;
        Assert.False(vm.IsResolvingPaths);
        Assert.True(vm.SyncCommand.CanExecute(null));
        Assert.True(vm.CanEditInputs);

        Directory.Delete(vm.TargetFolder, recursive: true);
    }
}
