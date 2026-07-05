using System.Collections.Concurrent;
using gclo.Engine;

namespace gclo.Cli.Tests;

/// <summary>Captures Console.Out and Console.Error for the duration of a block.</summary>
internal sealed class ConsoleCapture : IDisposable
{
    private readonly TextWriter _originalOut = Console.Out;
    private readonly TextWriter _originalError = Console.Error;
    private readonly StringWriter _out = new();
    private readonly StringWriter _error = new();

    public ConsoleCapture()
    {
        Console.SetOut(_out);
        Console.SetError(_error);
    }

    public string Out => _out.ToString();
    public string Error => _error.ToString();

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
    }
}

/// <summary>Redirects Console.In to a fixed string for the duration of a block.</summary>
internal sealed class StdinRedirect : IDisposable
{
    private readonly TextReader _original = Console.In;

    public StdinRedirect(string input) => Console.SetIn(new StringReader(input));

    public void Dispose() => Console.SetIn(_original);
}

/// <summary>An organization lister returning a fixed list or throwing a fixed exception.</summary>
internal sealed class FakeOrgLister : IOrganizationLister
{
    public IReadOnlyList<string> Result { get; set; } = [];
    public Exception? Throw { get; set; }

    public Task<IReadOnlyList<string>> ListOrganizationsAsync(string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Throw is not null
            ? Task.FromException<IReadOnlyList<string>>(Throw)
            : Task.FromResult(Result);
    }
}

/// <summary>A repository lister returning a fixed list or throwing a fixed exception.</summary>
internal sealed class FakeRepoLister : IRepositoryLister
{
    public IReadOnlyList<RepoDescriptor> Result { get; set; } = [];
    public Exception? Throw { get; set; }

    public Task<IReadOnlyList<RepoDescriptor>> ListOrganizationRepositoriesAsync(
        string organization, string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Throw is not null
            ? Task.FromException<IReadOnlyList<RepoDescriptor>>(Throw)
            : Task.FromResult(Result);
    }
}

/// <summary>A git client configured with delegates; records clone/pull/recovery calls.</summary>
internal sealed class FakeGit : IGitClient
{
    private readonly ConcurrentQueue<string> _clones = new();
    private readonly ConcurrentQueue<string> _recoveries = new();

    public Func<string, bool> IsValid { get; set; } = _ => false;
    public Func<string, string, string, Action<double>?, CancellationToken, Task> OnClone { get; set; }
        = (_, _, _, _, _) => Task.CompletedTask;
    public Func<string, string, CancellationToken, Task> OnPull { get; set; } = (_, _, _) => Task.CompletedTask;
    public Func<string, PathRecovery, CancellationToken, Task> OnApplyRecovery { get; set; } = (_, _, _) => Task.CompletedTask;

    public IReadOnlyList<string> ClonedRepoNames => _clones.Select(Path.GetFileName).ToArray()!;
    public IReadOnlyList<string> RecoveredRepoNames => _recoveries.Select(Path.GetFileName).ToArray()!;

    public bool IsValidRepository(string path) => IsValid(path);

    public Task CloneAsync(string url, string path, string token, Action<double>? onProgress, CancellationToken cancellationToken)
    {
        _clones.Enqueue(path);
        return OnClone(url, path, token, onProgress, cancellationToken);
    }

    public Task FetchAndPullAsync(string path, string token, CancellationToken cancellationToken)
        => OnPull(path, token, cancellationToken);

    public Task ApplyRecoveryAsync(string path, PathRecovery recovery, CancellationToken cancellationToken)
    {
        _recoveries.Enqueue(path);
        return OnApplyRecovery(path, recovery, cancellationToken);
    }
}

/// <summary>Discards every activity-log entry.</summary>
internal sealed class NullLog : IActivityLog
{
    public void Info(string message)
    {
    }

    public void Error(string message, Exception? exception = null)
    {
    }

    public string LogDirectory => "";
    public string CurrentLogFilePath => "";
}
