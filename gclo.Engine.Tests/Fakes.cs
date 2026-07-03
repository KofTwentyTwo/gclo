using System.Collections.Concurrent;
using gclo.Engine;

namespace gclo.Engine.Tests;

/// <summary>An <see cref="IRepositoryLister"/> that returns a configurable list (or throws).</summary>
public sealed class FakeRepositoryLister : IRepositoryLister
{
    private readonly ConcurrentQueue<(string Organization, string Token)> _calls = new();

    public IReadOnlyList<RepoDescriptor> Repositories { get; set; } = Array.Empty<RepoDescriptor>();

    /// <summary>When set, <see cref="ListOrganizationRepositoriesAsync"/> throws this instead of returning.</summary>
    public Exception? ExceptionToThrow { get; set; }

    public IReadOnlyList<(string Organization, string Token)> Calls => _calls.ToArray();

    public Task<IReadOnlyList<RepoDescriptor>> ListOrganizationRepositoriesAsync(
        string organization, string token, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue((organization, token));
        if (ExceptionToThrow is not null)
        {
            return Task.FromException<IReadOnlyList<RepoDescriptor>>(ExceptionToThrow);
        }
        return Task.FromResult(Repositories);
    }
}

public sealed record CloneCall(string Url, string LocalPath, string Token);
public sealed record PullCall(string LocalPath, string Token);

/// <summary>
/// An <see cref="IGitClient"/> whose behavior is configured with delegates.
/// All calls are recorded thread-safely; default handlers complete immediately.
/// </summary>
public sealed class FakeGitClient : IGitClient
{
    private readonly ConcurrentQueue<CloneCall> _cloneCalls = new();
    private readonly ConcurrentQueue<PullCall> _pullCalls = new();
    private readonly ConcurrentQueue<string> _validityChecks = new();

    /// <summary>Decides whether a local path counts as an existing valid repository. Default: never.</summary>
    public Func<string, bool> IsValidRepositoryHandler { get; set; } = _ => false;

    /// <summary>Body of <see cref="CloneAsync"/> (url, path, token, onProgress, ct). Default: completes immediately.</summary>
    public Func<string, string, string, Action<double>?, CancellationToken, Task> CloneHandler { get; set; }
        = (_, _, _, _, _) => Task.CompletedTask;

    /// <summary>Body of <see cref="FetchAndPullAsync"/> (path, token, ct). Default: completes immediately.</summary>
    public Func<string, string, CancellationToken, Task> FetchAndPullHandler { get; set; }
        = (_, _, _) => Task.CompletedTask;

    public IReadOnlyList<CloneCall> CloneCalls => _cloneCalls.ToArray();
    public IReadOnlyList<PullCall> PullCalls => _pullCalls.ToArray();
    public IReadOnlyList<string> ValidityChecks => _validityChecks.ToArray();

    /// <summary>Repo folder names (last path segment) passed to <see cref="CloneAsync"/>.</summary>
    public IReadOnlyList<string> ClonedRepoNames
        => _cloneCalls.Select(c => System.IO.Path.GetFileName(c.LocalPath)!).ToArray();

    /// <summary>Repo folder names (last path segment) passed to <see cref="FetchAndPullAsync"/>.</summary>
    public IReadOnlyList<string> PulledRepoNames
        => _pullCalls.Select(c => System.IO.Path.GetFileName(c.LocalPath)!).ToArray();

    public bool IsValidRepository(string path)
    {
        _validityChecks.Enqueue(path);
        return IsValidRepositoryHandler(path);
    }

    public async Task CloneAsync(string url, string path, string token, Action<double>? onProgress, CancellationToken cancellationToken)
    {
        _cloneCalls.Enqueue(new CloneCall(url, path, token));
        await CloneHandler(url, path, token, onProgress, cancellationToken).ConfigureAwait(false);
    }

    public async Task FetchAndPullAsync(string path, string token, CancellationToken cancellationToken)
    {
        _pullCalls.Enqueue(new PullCall(path, token));
        await FetchAndPullHandler(path, token, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>An <see cref="IOrganizationLister"/> whose behavior is configured with a delegate.</summary>
public sealed class FakeOrganizationLister : IOrganizationLister
{
    private int _calls;

    public Func<string, CancellationToken, Task<IReadOnlyList<string>>> Handler { get; set; }
        = (_, _) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public int Calls => _calls;

    public Task<IReadOnlyList<string>> ListOrganizationsAsync(string token, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return Handler(token, cancellationToken);
    }
}

/// <summary>
/// Records every report synchronously into a thread-safe queue.
/// Deliberately NOT <see cref="Progress{T}"/>, which posts callbacks to a sync context
/// asynchronously and would make assertions racy.
/// </summary>
public sealed class RecordingProgress : IProgress<RepoProgress>
{
    private readonly ConcurrentQueue<RepoProgress> _reports = new();

    public void Report(RepoProgress value) => _reports.Enqueue(value);

    /// <summary>All reports in the order they were recorded.</summary>
    public IReadOnlyList<RepoProgress> Reports => _reports.ToArray();

    /// <summary>Ordered statuses reported for one repository.</summary>
    public IReadOnlyList<SyncStatus> StatusesFor(string repoName)
        => _reports.Where(r => r.RepoName == repoName).Select(r => r.Status).ToArray();

    /// <summary>The most recent report for one repository, or null if none.</summary>
    public RepoProgress? LastFor(string repoName)
        => _reports.LastOrDefault(r => r.RepoName == repoName);
}
