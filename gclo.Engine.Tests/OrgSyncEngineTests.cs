using System.Diagnostics;
using gclo.Engine;

namespace gclo.Engine.Tests;

public sealed class OrgSyncEngineTests : IDisposable
{
    private static readonly SyncStatus[] TerminalStatuses =
        [SyncStatus.Done, SyncStatus.Failed, SyncStatus.Canceled];

    private readonly string _targetRoot =
        Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

    private readonly FakeRepositoryLister _lister = new();
    private readonly FakeGitClient _git = new();
    private readonly RecordingProgress _progress = new();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_targetRoot))
            {
                Directory.Delete(_targetRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort; stray empty temp dirs are harmless.
        }
    }

    private OrgSyncEngine CreateEngine() => new(_lister, _git);

    private SyncRequest CreateRequest(int maxConcurrency = 8)
        => new("acme", "test-token", _targetRoot, maxConcurrency);

    private static RepoDescriptor Repo(string name)
        => new(name, $"https://example.test/acme/{name}.git", "main", IsArchived: false);

    private static RepoDescriptor[] Repos(params string[] names) => names.Select(Repo).ToArray();

    /// <summary>Polls until <paramref name="condition"/> is true; fails the test after a generous timeout.</summary>
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

    // ---------------------------------------------------------------- (a)

    [Fact]
    public async Task Sync_ClonesMissingRepos_AndPullsExistingOnes()
    {
        _lister.Repositories = Repos("alpha", "bravo", "charlie", "delta");
        var locallyValid = new HashSet<string> { "alpha", "charlie" };
        _git.IsValidRepositoryHandler = path => locallyValid.Contains(Path.GetFileName(path)!);

        var summary = await CreateEngine().SyncAsync(CreateRequest(), _progress);

        Assert.Equal(new[] { "bravo", "delta" }, _git.ClonedRepoNames.Order().ToArray());
        Assert.Equal(new[] { "alpha", "charlie" }, _git.PulledRepoNames.Order().ToArray());

        // Calls carry the right paths, URLs, and token.
        foreach (var call in _git.CloneCalls)
        {
            var name = Path.GetFileName(call.LocalPath)!;
            Assert.Equal(Path.Combine(_targetRoot, name), call.LocalPath);
            Assert.Equal($"https://example.test/acme/{name}.git", call.Url);
            Assert.Equal("test-token", call.Token);
        }
        foreach (var call in _git.PullCalls)
        {
            Assert.Equal(Path.Combine(_targetRoot, Path.GetFileName(call.LocalPath)!), call.LocalPath);
            Assert.Equal("test-token", call.Token);
        }

        Assert.Equal(new SyncSummary(Total: 4, Cloned: 2, Updated: 2, Failed: 0, Canceled: 0, WasCanceled: false), summary);
        Assert.Equal([("acme", "test-token")], _lister.Calls);
        Assert.True(Directory.Exists(_targetRoot), "engine should create TargetRoot");
    }

    // ---------------------------------------------------------------- (b)

    [Fact]
    public async Task Sync_NeverExceedsMaxConcurrency_ButActuallyRunsInParallel()
    {
        const int repoCount = 20;
        const int maxConcurrency = 3;
        _lister.Repositories = Repos(Enumerable.Range(0, repoCount).Select(i => $"repo{i:D2}").ToArray());

        int inFlight = 0;
        int maxObserved = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _git.CloneHandler = async (_, _, _, _, _) =>
        {
            int now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref maxObserved, now);
            try
            {
                // Pile up here until the test releases the gate. The timeout is a safety
                // valve so a regression cannot hang the test run; the wait is deliberately
                // not cancelable (CancellationToken.None).
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        };

        var syncTask = CreateEngine().SyncAsync(CreateRequest(maxConcurrency), _progress);

        // Wait until the limit is actually saturated, proving real parallelism.
        await WaitUntilAsync(() => Volatile.Read(ref maxObserved) >= maxConcurrency,
            $"{maxConcurrency} concurrent git operations in flight");

        gate.SetResult();
        var summary = await syncTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(Volatile.Read(ref maxObserved) <= maxConcurrency,
            $"observed {maxObserved} concurrent operations; limit is {maxConcurrency}");
        Assert.Equal(maxConcurrency, Volatile.Read(ref maxObserved));
        Assert.Equal(new SyncSummary(repoCount, Cloned: repoCount, Updated: 0, Failed: 0, Canceled: 0, WasCanceled: false), summary);
    }

    private static void InterlockedMax(ref int location, int value)
    {
        int snapshot;
        while (value > (snapshot = Volatile.Read(ref location)))
        {
            if (Interlocked.CompareExchange(ref location, value, snapshot) == snapshot)
            {
                break;
            }
        }
    }

    // ---------------------------------------------------------------- (c)

    [Fact]
    public async Task Sync_OneRepoFailing_DoesNotAbortTheOthers()
    {
        string[] names = ["r0", "r1", "r2", "r3", "r4"];
        _lister.Repositories = Repos(names);
        _git.CloneHandler = (_, path, _, _, _) =>
            Path.GetFileName(path) == "r2"
                ? Task.FromException(new InvalidOperationException("boom"))
                : Task.CompletedTask;

        var summary = await CreateEngine().SyncAsync(CreateRequest(), _progress);

        Assert.Equal(new SyncSummary(Total: 5, Cloned: 4, Updated: 0, Failed: 1, Canceled: 0, WasCanceled: false), summary);

        var failedReport = Assert.Single(_progress.Reports, r => r.Status == SyncStatus.Failed);
        Assert.Equal("r2", failedReport.RepoName);
        Assert.NotNull(failedReport.Error);
        Assert.Contains("boom", failedReport.Error);

        // Every repo reached exactly one terminal state; r2 failed, the rest are Done.
        foreach (var name in names)
        {
            var statuses = _progress.StatusesFor(name);
            Assert.Single(statuses, s => TerminalStatuses.Contains(s));
            Assert.Equal(name == "r2" ? SyncStatus.Failed : SyncStatus.Done, statuses[^1]);
        }
    }

    // ---------------------------------------------------------------- (d)

    [Fact]
    public async Task Sync_Cancellation_MarksInFlightAndUnstartedReposCanceled()
    {
        string[] names = ["c0", "c1", "c2", "c3", "c4", "c5"];
        _lister.Repositories = Repos(names);

        int entered = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _git.CloneHandler = async (_, _, _, _, ct) =>
        {
            Interlocked.Increment(ref entered);
            await gate.Task.WaitAsync(ct); // observes ct: throws OperationCanceledException on cancel
            ct.ThrowIfCancellationRequested(); // even if the gate wins the race, honor cancellation
        };

        using var cts = new CancellationTokenSource();
        var syncTask = CreateEngine().SyncAsync(CreateRequest(maxConcurrency: 2), _progress, cts.Token);

        // Both worker slots are occupied and blocked on the gate.
        await WaitUntilAsync(() => Volatile.Read(ref entered) == 2, "2 clone operations in flight");

        cts.Cancel();
        gate.TrySetResult(); // release the gate; in-flight ops observe ct and throw

        var summary = await syncTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(summary.WasCanceled);
        Assert.True(summary.Canceled > 0, "at least the in-flight repos must be Canceled");
        Assert.Equal(names.Length, summary.Total);
        Assert.Equal(summary.Total, summary.Cloned + summary.Updated + summary.Failed + summary.Canceled);
        Assert.Equal(new SyncSummary(Total: 6, Cloned: 0, Updated: 0, Failed: 0, Canceled: 6, WasCanceled: true), summary);

        // Every repo reached a terminal state; none is stuck at Queued/Cloning/Pulling.
        foreach (var name in names)
        {
            var last = _progress.LastFor(name);
            Assert.NotNull(last);
            Assert.Contains(last.Status, TerminalStatuses);
            Assert.Equal(SyncStatus.Canceled, last.Status);
        }
    }

    // ---------------------------------------------------------------- (e)

    [Fact]
    public async Task Sync_ClonePath_ReportsQueuedCloningDoneInOrder()
    {
        _lister.Repositories = Repos("solo");
        _git.IsValidRepositoryHandler = _ => false;

        var summary = await CreateEngine().SyncAsync(CreateRequest(), _progress);

        Assert.Equal([SyncStatus.Queued, SyncStatus.Cloning, SyncStatus.Done], _progress.StatusesFor("solo"));
        Assert.Equal(new SyncSummary(1, Cloned: 1, Updated: 0, Failed: 0, Canceled: 0, WasCanceled: false), summary);
    }

    [Fact]
    public async Task Sync_PullPath_ReportsQueuedPullingDoneInOrder()
    {
        _lister.Repositories = Repos("solo");
        _git.IsValidRepositoryHandler = _ => true;

        var summary = await CreateEngine().SyncAsync(CreateRequest(), _progress);

        Assert.Equal([SyncStatus.Queued, SyncStatus.Pulling, SyncStatus.Done], _progress.StatusesFor("solo"));
        Assert.Equal(new SyncSummary(1, Cloned: 0, Updated: 1, Failed: 0, Canceled: 0, WasCanceled: false), summary);
        Assert.Empty(_git.CloneCalls);
    }

    [Fact]
    public async Task Sync_CloneProgressCallback_IsForwardedAsCloningPercent()
    {
        _lister.Repositories = Repos("solo");
        _git.CloneHandler = (_, _, _, onProgress, _) =>
        {
            onProgress?.Invoke(0.25);
            onProgress?.Invoke(0.75);
            return Task.CompletedTask;
        };

        await CreateEngine().SyncAsync(CreateRequest(), _progress);

        var reports = _progress.Reports.Where(r => r.RepoName == "solo").ToArray();
        Assert.Equal(
            [
                (SyncStatus.Queued, (double?)null),
                (SyncStatus.Cloning, null),
                (SyncStatus.Cloning, 0.25),
                (SyncStatus.Cloning, 0.75),
                (SyncStatus.Done, null),
            ],
            reports.Select(r => (r.Status, r.Percent)).ToArray());
    }

    // ---------------------------------------------------------------- (f)

    [Fact]
    public async Task Sync_EmptyOrganization_ReturnsZeroTotalsAndMakesNoGitCalls()
    {
        _lister.Repositories = [];

        var summary = await CreateEngine().SyncAsync(CreateRequest(), _progress);

        Assert.Equal(new SyncSummary(Total: 0, Cloned: 0, Updated: 0, Failed: 0, Canceled: 0, WasCanceled: false), summary);
        Assert.Empty(_git.CloneCalls);
        Assert.Empty(_git.PullCalls);
        Assert.Empty(_git.ValidityChecks);
        Assert.Empty(_progress.Reports);
    }

    // ---------------------------------------------------------------- request validation & lister errors

    [Fact]
    public async Task Sync_NullRequest_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => CreateEngine().SyncAsync(null!));
    }

    [Theory]
    [InlineData("", "token")]
    [InlineData("   ", "token")]
    [InlineData("org", "")]
    [InlineData("org", "   ")]
    public async Task Sync_BlankOrganizationOrToken_ThrowsArgumentException(string organization, string token)
    {
        var request = new SyncRequest(organization, token, _targetRoot);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => CreateEngine().SyncAsync(request));
        Assert.Empty(_lister.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Sync_BlankTargetRoot_ThrowsArgumentException(string targetRoot)
    {
        var request = new SyncRequest("org", "token", targetRoot);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => CreateEngine().SyncAsync(request));
        Assert.Empty(_lister.Calls);
    }

    [Fact]
    public async Task Sync_ListerFailure_PropagatesAndMakesNoGitCalls()
    {
        _lister.ExceptionToThrow = new InvalidOperationException("org not found");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateEngine().SyncAsync(CreateRequest(), _progress));

        Assert.Equal("org not found", ex.Message);
        Assert.Empty(_git.CloneCalls);
        Assert.Empty(_git.PullCalls);
        Assert.Empty(_progress.Reports);
    }

    [Fact]
    public async Task Sync_TargetRootCreationFails_PropagatesWithNoProgressReports()
    {
        // A file sitting where the target directory should go makes CreateDirectory throw.
        Directory.CreateDirectory(Path.GetDirectoryName(_targetRoot)!);
        File.WriteAllText(_targetRoot, "blocking file");
        try
        {
            _lister.Repositories = Repos("alpha");

            await Assert.ThrowsAsync<IOException>(
                () => CreateEngine().SyncAsync(CreateRequest(), _progress));

            // Failing before any Queued report means no repo is stranded mid-state.
            Assert.Empty(_progress.Reports);
            Assert.Empty(_git.CloneCalls);
            Assert.Empty(_git.PullCalls);
        }
        finally
        {
            File.Delete(_targetRoot); // Dispose only handles directories
        }
    }

    [Fact]
    public async Task Sync_ReportsAllReposQueued_BeforeAnyGitWork()
    {
        var names = new[] { "alpha", "bravo", "charlie", "delta", "echo" };
        _lister.Repositories = Repos(names);

        await CreateEngine().SyncAsync(CreateRequest(), _progress);

        // The engine queues the full list synchronously before the parallel phase,
        // so the first N reports must be Queued and cover every repo exactly once.
        var first = _progress.Reports.Take(names.Length).ToArray();
        Assert.All(first, r => Assert.Equal(SyncStatus.Queued, r.Status));
        Assert.Equal(names.Order().ToArray(), first.Select(r => r.RepoName).Order().ToArray());
    }

    [Fact]
    public async Task Sync_OceWithoutCancellation_CountsAsFailedAndDoesNotStopOthers()
    {
        _lister.Repositories = Repos("alpha", "bravo", "charlie");
        _git.CloneHandler = (url, path, token, onProgress, ct) =>
            Path.GetFileName(path) == "bravo"
                ? Task.FromException(new OperationCanceledException("spurious"))
                : Task.CompletedTask;

        var summary = await CreateEngine().SyncAsync(CreateRequest(), _progress);

        // An OperationCanceledException while the caller's token is NOT canceled is a
        // repo failure, not a cancellation: the run must complete the other repos.
        Assert.Equal(new SyncSummary(Total: 3, Cloned: 2, Updated: 0, Failed: 1, Canceled: 0, WasCanceled: false), summary);
        Assert.Equal(SyncStatus.Failed, _progress.LastFor("bravo")!.Status);
        Assert.Equal(SyncStatus.Done, _progress.LastFor("alpha")!.Status);
        Assert.Equal(SyncStatus.Done, _progress.LastFor("charlie")!.Status);
    }

    [Fact]
    public async Task Sync_DuplicateRepoNames_AreProcessedOnce()
    {
        _lister.Repositories =
        [
            Repo("alpha"),
            new RepoDescriptor("Alpha", "https://example.test/acme/Alpha.git", "main", IsArchived: false),
        ];

        var summary = await CreateEngine().SyncAsync(CreateRequest(), _progress);

        Assert.Equal(1, summary.Total);
        Assert.Single(_git.CloneCalls);
    }
}
