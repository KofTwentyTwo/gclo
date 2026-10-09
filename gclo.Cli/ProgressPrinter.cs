using System.Text.Json;
using gclo.Engine;

namespace gclo.Cli;

/// <summary>How 'gclo sync' reports progress and the result.</summary>
internal enum OutputMode
{
    /// <summary>Transitions on stdout, failures on stderr, a summary line at the end.</summary>
    Text,

    /// <summary>Failures on stderr and the summary line only.</summary>
    Quiet,

    /// <summary>Nothing until the end, then one JSON document.</summary>
    Json,

    /// <summary>One JSON object per transition on stdout (NDJSON), then the summary object.</summary>
    JsonLines,
}

/// <summary>
/// Prints one line per repository status transition and records failures (and
/// sanitized repositories) for the final summary. Clone percent updates arrive as
/// repeated <see cref="SyncStatus.Cloning"/> snapshots and are suppressed — only
/// actual status transitions print. In the text modes failure lines go to stderr so
/// they survive '--quiet' and stdout redirection; the JSON modes carry failures in
/// the document instead.
/// </summary>
internal sealed class ProgressPrinter : IProgress<RepoProgress>
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SyncStatus> _lastStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SyncFailure> _failures = new();
    private readonly List<SyncSanitized> _sanitized = new();
    private readonly OutputMode _mode;

    public ProgressPrinter(OutputMode mode) => _mode = mode;

    /// <summary>Failed repositories in the order they failed.</summary>
    public IReadOnlyList<SyncFailure> Failures
    {
        get
        {
            lock (_gate)
            {
                return _failures.ToArray();
            }
        }
    }

    /// <summary>Repositories checked out with sanitized paths, in order.</summary>
    public IReadOnlyList<SyncSanitized> Sanitized
    {
        get
        {
            lock (_gate)
            {
                return _sanitized.ToArray();
            }
        }
    }

    /// <summary>Records a '--sanitize-paths' outcome for the summary document.</summary>
    public void RecordSanitized(SyncSanitized sanitized)
    {
        lock (_gate)
        {
            _sanitized.Add(sanitized);
        }
    }

    // The engine reports from worker threads; the lock keeps lines whole and in
    // a single consistent order.
    public void Report(RepoProgress value)
    {
        lock (_gate)
        {
            if (_lastStatus.TryGetValue(value.RepoName, out SyncStatus previous) && previous == value.Status)
            {
                return; // e.g. clone percent updates re-report Cloning
            }
            _lastStatus[value.RepoName] = value.Status;

            if (value.Status == SyncStatus.Failed)
            {
                var failure = new SyncFailure(value.RepoName, value.Error ?? "unknown error", MapInvalidPaths(value.InvalidPaths));
                _failures.Add(failure);
                switch (_mode)
                {
                    case OutputMode.Text or OutputMode.Quiet:
                        Console.Error.WriteLine($"{failure.Repo}  Failed  {failure.Error}");
                        break;
                    case OutputMode.JsonLines:
                        WriteProgressLine(value.RepoName, value.Status, failure.Error);
                        break;
                    default:
                        break; // Json: the document carries it
                }
                return;
            }

            switch (_mode)
            {
                case OutputMode.Text:
                    Console.Out.WriteLine($"{value.RepoName}  {value.Status}");
                    break;
                case OutputMode.JsonLines:
                    WriteProgressLine(value.RepoName, value.Status, null);
                    break;
                default:
                    break; // Quiet, Json: transitions are not printed
            }
        }
    }

    private static void WriteProgressLine(string repo, SyncStatus status, string? error)
        => Console.Out.WriteLine(JsonSerializer.Serialize(
            new SyncProgressLine("progress", repo, status.ToString(), error), CliJsonContext.Default.SyncProgressLine));

    /// <summary>The full offending-path list for the document; null when the failure was not path validation.</summary>
    internal static IReadOnlyList<SyncInvalidPath>? MapInvalidPaths(IReadOnlyList<InvalidPathInfo>? paths)
        => paths is { Count: > 0 }
            ? paths.Select(p => new SyncInvalidPath(p.RepoPath, p.Reason, p.SuggestedName)).ToList()
            : null;
}
