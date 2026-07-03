using gclo.Engine;
using gclo.ViewModels;
using static gclo.Engine.Tests.GitTestHelpers;

namespace gclo.Engine.Tests;

/// <summary>
/// Headless tests for <see cref="MainViewModel"/> using a synchronous progress factory
/// (the production default, <see cref="Progress{T}"/>, posts asynchronously and would
/// make assertions racy) and a near-zero org-lookup debounce.
/// </summary>
public sealed class MainViewModelTests
{
    private readonly FakeRepositoryLister _lister = new();
    private readonly FakeGitClient _git = new();
    private readonly FakeOrganizationLister _orgs = new();

    /// <summary>Synchronous progress: handler runs inline on the reporting thread.</summary>
    private sealed class SyncProgress(Action<RepoProgress> handler) : IProgress<RepoProgress>
    {
        public void Report(RepoProgress value) => handler(value);
    }

    private MainViewModel CreateViewModel(TimeSpan? debounce = null)
        => new(_lister, _git, _orgs,
               handler => new SyncProgress(handler),
               debounce ?? TimeSpan.FromMilliseconds(1),
               new NullActivityLog());

    /// <summary>
    /// A view model with valid inputs and the given repositories already loaded into the
    /// table. Waits out the token-triggered org lookup first so its status message cannot
    /// land on top of a later assertion.
    /// </summary>
    private async Task<MainViewModel> CreateLoadedViewModelAsync(params RepoDescriptor[] repos)
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
        Assert.Equal(3, vm.TotalCount);
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
        Assert.Equal(3, vm.CompletedCount);
        Assert.False(vm.RetryFailedCommand.CanExecute(null)); // nothing failed anymore

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
        Assert.Contains(nameof(MainViewModel.EffectiveTargetRoot), raised);
        Assert.Contains(nameof(MainViewModel.TargetPreview), raised);
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
}
