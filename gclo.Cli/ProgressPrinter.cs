using gclo.Engine;

namespace gclo.Cli;

/// <summary>
/// Prints one line per repository status transition and records failures for the
/// final summary. Clone percent updates arrive as repeated
/// <see cref="SyncStatus.Cloning"/> snapshots and are suppressed — only actual
/// status transitions print. Failure lines go to stderr so they survive
/// '--quiet' and stdout redirection; everything else goes to stdout.
/// </summary>
internal sealed class ProgressPrinter : IProgress<RepoProgress>
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SyncStatus> _lastStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SyncFailure> _failures = new();
    private readonly bool _printProgress;
    private readonly bool _printFailures;

    /// <param name="printProgress">Write non-failure transitions to stdout (off for --quiet and --json).</param>
    /// <param name="printFailures">Write failure lines to stderr (off for --json, which carries failures in the document).</param>
    public ProgressPrinter(bool printProgress, bool printFailures)
    {
        _printProgress = printProgress;
        _printFailures = printFailures;
    }

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
                var failure = new SyncFailure(value.RepoName, value.Error ?? "unknown error");
                _failures.Add(failure);
                if (_printFailures)
                {
                    Console.Error.WriteLine($"{failure.Repo}  Failed  {failure.Error}");
                }
            }
            else if (_printProgress)
            {
                Console.Out.WriteLine($"{value.RepoName}  {value.Status}");
            }
        }
    }
}
