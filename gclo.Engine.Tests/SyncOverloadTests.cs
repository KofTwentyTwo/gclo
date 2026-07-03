using gclo.Engine;
using static gclo.Engine.Tests.GitTestHelpers;

namespace gclo.Engine.Tests;

/// <summary>
/// Tests for the <see cref="OrgSyncEngine.SyncAsync(SyncRequest, IReadOnlyList{RepoDescriptor},
/// IProgress{RepoProgress}?, CancellationToken)"/> overload that takes a caller-supplied
/// repository list: it must never hit the lister, and must otherwise behave exactly like
/// the listing overload (dedupe, Queued-first reporting, root creation, counting).
/// </summary>
public sealed class SyncOverloadTests : IDisposable
{
    private readonly string _targetRoot =
        Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

    private readonly FakeRepositoryLister _lister = new();
    private readonly FakeGitClient _git = new();
    private readonly RecordingProgress _progress = new();

    public void Dispose() => TryDeleteDirectory(_targetRoot);

    private OrgSyncEngine CreateEngine() => new(_lister, _git);

    private SyncRequest CreateRequest(int maxConcurrency = 8)
        => new("acme", "test-token", _targetRoot, maxConcurrency);

    [Fact]
    public async Task SyncWithList_NeverCallsTheLister()
    {
        // Give the lister a different answer so any accidental call is caught.
        _lister.Repositories = Repos("from-lister");

        var summary = await CreateEngine().SyncAsync(CreateRequest(), Repos("alpha", "bravo"), _progress);

        Assert.Empty(_lister.Calls);
        Assert.Equal(new[] { "alpha", "bravo" }, _git.ClonedRepoNames.Order().ToArray());
        Assert.Equal(new SyncSummary(Total: 2, Cloned: 2, Updated: 0, Failed: 0, Canceled: 0, WasCanceled: false), summary);
    }

    [Fact]
    public async Task SyncWithList_SyncsExactlyTheGivenSelection()
    {
        // A caller that filtered a larger set (deselected repos) passes only the
        // selection; nothing outside it may be touched or reported.
        _lister.Repositories = Repos("alpha", "bravo", "charlie", "delta");

        var summary = await CreateEngine().SyncAsync(CreateRequest(), Repos("bravo", "delta"), _progress);

        Assert.Equal(new[] { "bravo", "delta" }, _git.ClonedRepoNames.Order().ToArray());
        Assert.All(_progress.Reports, r => Assert.Contains(r.RepoName, new[] { "bravo", "delta" }));
        Assert.Equal(2, summary.Total);
        Assert.Empty(_lister.Calls);
    }

    [Fact]
    public async Task SyncWithList_ReportsQueuedForEveryRepo_BeforeAnyGitWork()
    {
        var names = new[] { "alpha", "bravo", "charlie", "delta", "echo" };

        await CreateEngine().SyncAsync(CreateRequest(), Repos(names), _progress);

        // The engine queues the full list synchronously before the parallel phase,
        // so the first N reports must be Queued and cover every repo exactly once.
        var first = _progress.Reports.Take(names.Length).ToArray();
        Assert.All(first, r => Assert.Equal(SyncStatus.Queued, r.Status));
        Assert.Equal(names.Order().ToArray(), first.Select(r => r.RepoName).Order().ToArray());
    }

    [Fact]
    public async Task SyncWithList_DuplicateRepoNames_AreProcessedOnce()
    {
        RepoDescriptor[] repositories =
        [
            Repo("alpha"),
            new RepoDescriptor("Alpha", "https://example.test/acme/Alpha.git", "main", IsArchived: false),
        ];

        var summary = await CreateEngine().SyncAsync(CreateRequest(), repositories, _progress);

        Assert.Equal(1, summary.Total);
        Assert.Single(_git.CloneCalls);
    }

    [Fact]
    public async Task SyncWithList_CountsClonesUpdatesAndFailures()
    {
        var locallyValid = new HashSet<string> { "alpha", "charlie" };
        _git.IsValidRepositoryHandler = path => locallyValid.Contains(Path.GetFileName(path)!);
        _git.CloneHandler = (_, path, _, _, _) =>
            Path.GetFileName(path) == "echo"
                ? Task.FromException(new InvalidOperationException("boom"))
                : Task.CompletedTask;

        var summary = await CreateEngine().SyncAsync(
            CreateRequest(), Repos("alpha", "bravo", "charlie", "delta", "echo"), _progress);

        Assert.Equal(new SyncSummary(Total: 5, Cloned: 2, Updated: 2, Failed: 1, Canceled: 0, WasCanceled: false), summary);
        Assert.Equal(new[] { "alpha", "charlie" }, _git.PulledRepoNames.Order().ToArray());
        Assert.Equal(SyncStatus.Failed, _progress.LastFor("echo")!.Status);
        Assert.Equal(SyncStatus.Done, _progress.LastFor("bravo")!.Status);
        Assert.Equal(SyncStatus.Done, _progress.LastFor("delta")!.Status);
    }

    [Fact]
    public async Task SyncWithList_CreatesTargetRoot()
    {
        await CreateEngine().SyncAsync(CreateRequest(), Repos("alpha"), _progress);

        Assert.True(Directory.Exists(_targetRoot), "engine should create TargetRoot");
    }

    [Fact]
    public async Task SyncWithList_EmptyList_ReturnsZeroTotalsAndMakesNoGitCalls()
    {
        var summary = await CreateEngine().SyncAsync(CreateRequest(), Array.Empty<RepoDescriptor>(), _progress);

        Assert.Equal(new SyncSummary(Total: 0, Cloned: 0, Updated: 0, Failed: 0, Canceled: 0, WasCanceled: false), summary);
        Assert.Empty(_git.CloneCalls);
        Assert.Empty(_git.PullCalls);
        Assert.Empty(_git.ValidityChecks);
        Assert.Empty(_progress.Reports);
        Assert.Empty(_lister.Calls);
    }

    [Fact]
    public async Task SyncWithList_NullRepositories_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => CreateEngine().SyncAsync(CreateRequest(), (IReadOnlyList<RepoDescriptor>)null!));
    }
}
