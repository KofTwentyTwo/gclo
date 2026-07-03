using System.Diagnostics;
using gclo.Engine;
using gclo.ViewModels;

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
               debounce ?? TimeSpan.FromMilliseconds(1));

    private static RepoDescriptor Repo(string name)
        => new(name, $"https://example.test/acme/{name}.git", "main", IsArchived: false);

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15),
                $"Timed out after {stopwatch.Elapsed} waiting for: {description}");
            await Task.Delay(10);
        }
    }

    // ---------------------------------------------------------------- command gating

    [Fact]
    public void SyncCommand_RequiresAllInputs_AndNotRunning()
    {
        var vm = CreateViewModel();
        Assert.False(vm.SyncCommand.CanExecute(null));

        vm.Organization = "acme";
        vm.Token = "token-1234567890";
        Assert.False(vm.SyncCommand.CanExecute(null));

        vm.TargetFolder = @"C:\src\acme";
        Assert.True(vm.SyncCommand.CanExecute(null));
    }

    // ---------------------------------------------------------------- sync flow

    [Fact]
    public async Task Sync_PopulatesItems_TracksCounts_AndSummarizesFailures()
    {
        _lister.Repositories = [Repo("alpha"), Repo("bravo"), Repo("charlie")];
        _git.IsValidRepositoryHandler = path => Path.GetFileName(path) == "alpha"; // alpha pulls
        _git.CloneHandler = (url, path, token, onProgress, ct) =>
            Path.GetFileName(path) == "charlie"
                ? Task.FromException(new InvalidOperationException("boom"))
                : Task.CompletedTask;

        var vm = CreateViewModel();
        vm.Organization = "acme";
        vm.Token = "token-1234567890";
        vm.TargetFolder = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));
        vm.MaxConcurrencyValue = 1; // serialize progress: the sync fake reports inline across threads

        await vm.SyncCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.Repos.Count);
        Assert.Equal(3, vm.TotalCount);
        Assert.Equal(3, vm.CompletedCount);
        Assert.False(vm.IsRunning);

        var byName = vm.Repos.ToDictionary(r => r.Name);
        Assert.Equal(SyncStatus.Done, byName["alpha"].Status);
        Assert.Equal(SyncStatus.Done, byName["bravo"].Status);
        Assert.Equal(SyncStatus.Failed, byName["charlie"].Status);
        Assert.Contains("boom", byName["charlie"].Error);
        Assert.Contains("1 failed", vm.StatusText);

        Directory.Delete(vm.TargetFolder, recursive: true);
    }

    [Fact]
    public async Task Sync_ListerFailure_SurfacesMessage_AndResetsRunning()
    {
        _lister.ExceptionToThrow = new InvalidOperationException("org not found");

        var vm = CreateViewModel();
        vm.Organization = "acme";
        vm.Token = "token-1234567890";
        vm.TargetFolder = @"C:\src\acme";

        await vm.SyncCommand.ExecuteAsync(null);

        Assert.Equal("org not found", vm.StatusText);
        Assert.Empty(vm.Repos);
        Assert.False(vm.IsRunning);
        Assert.True(vm.SyncCommand.CanExecute(null));
    }

    [Fact]
    public async Task Sync_CancelCommand_CancelsRun_AndReportsCanceled()
    {
        _lister.Repositories = [Repo("alpha"), Repo("bravo")];
        var gate = new TaskCompletionSource();
        int started = 0;
        _git.CloneHandler = async (url, path, token, onProgress, ct) =>
        {
            Interlocked.Increment(ref started);
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(30));
            ct.ThrowIfCancellationRequested();
        };

        var vm = CreateViewModel();
        vm.Organization = "acme";
        vm.Token = "token-1234567890";
        vm.TargetFolder = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));
        vm.MaxConcurrencyValue = 1;

        Task run = vm.SyncCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => started > 0, "first clone to start");

        vm.SyncCancelCommand.Execute(null);
        gate.SetResult();
        await run;

        Assert.StartsWith("Canceled", vm.StatusText);
        Assert.False(vm.IsRunning);
        Assert.All(vm.Repos, r => Assert.True(
            r.Status is SyncStatus.Canceled or SyncStatus.Done or SyncStatus.Failed,
            $"{r.Name} left in non-terminal state {r.Status}"));

        Directory.Delete(vm.TargetFolder, recursive: true);
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
    public async Task TokenChange_EmptyOrgList_ExplainsAndAllowsManualEntry()
    {
        var vm = CreateViewModel();

        vm.Token = "token-1234567890";

        await WaitUntilAsync(() => vm.StatusText.Length > 0, "status text");
        Assert.Contains("read:org", vm.StatusText);
        Assert.Empty(vm.Organizations);
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
    public void RepoItem_StatusText_ShowsClonePercent_AndErrorFlag()
    {
        var item = new RepoItemViewModel("alpha");
        Assert.Equal("Queued", item.StatusText);
        Assert.False(item.HasError);

        item.Status = SyncStatus.Cloning;
        item.Percent = 0.42;
        Assert.Equal("Cloning 42%", item.StatusText);

        item.Status = SyncStatus.Failed;
        item.Error = "boom";
        Assert.True(item.HasError);
    }
}
