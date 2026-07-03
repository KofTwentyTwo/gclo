using System.Text.Json;
using gclo.Engine;

namespace gclo.Cli;

/// <summary>'gclo sync': clone or fast-forward every repository of an organization.</summary>
internal static class SyncCommand
{
    private const string HelpText = """
        Usage: gclo sync --org <name> --target <folder> [options]

        Clones every repository of <name> into <folder>\<repo>. Repositories that
        already exist locally are fetched and fast-forwarded instead. Repositories
        fail independently; one failure never stops the rest.

        Options:
          --org <name>         GitHub organization or user account to sync (required).
          --target <folder>    Local root folder; each repository becomes a subfolder
                               (required; created if missing).
          --parallel <N>       Maximum simultaneous git operations (default 8).
          --sanitize-paths     When a repository contains paths that are legal in
                               git but invalid on Windows (reserved device names,
                               characters like ':', trailing dots or spaces),
                               rename them to safe suggested names instead of
                               failing the repository. Paths with no safe rename
                               (case-only collisions) are skipped. The renames are
                               listed on stderr and in the activity log, and the
                               repository counts as cloned.
          --token-env <VAR>    Read the token from environment variable VAR
                               (default: GITHUB_TOKEN when no token option is given).
          --token-file <path>  Read the token from the first line of a file.
          --token-stdin        Read the token as one line from standard input.
          --json               Print no progress lines; end with one line of JSON:
                               {"total":N,"cloned":N,"updated":N,"failed":N,"canceled":N,
                                "wasCanceled":bool,"failures":[{"repo":"...","error":"..."}]}
          --quiet              Suppress per-repository progress lines on stdout.
                               Failures still print to stderr and the summary still prints.
          --help               Show this help.

        Output:
          One line per repository status transition, '<name>  <status>', with
          statuses Queued, Cloning, Pulling, Done, Canceled on stdout and
          '<name>  Failed  <error>' on stderr. Clone percent updates are not printed.

          A repository whose paths cannot be created on Windows fails with the
          offending paths listed on stderr (at most 10, plus a count of the
          rest). With --sanitize-paths it is checked out with those paths
          renamed or skipped instead and counts as cloned.

          Ctrl+C cancels gracefully: in-flight git operations stop, remaining
          repositories are marked Canceled, and the summary still prints.

        Exit codes:
          0  every repository synced
          1  run completed but some repositories failed, or it was canceled
          2  fatal: bad arguments, missing or rejected token, organization not found

        Security:
          There is deliberately no '--token <value>' option: process command lines
          are visible to other processes on the machine, so a token passed as an
          argument would leak. Use --token-env, --token-file, or --token-stdin.
        """;

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        string? org = null;
        string? target = null;
        int parallel = 8;
        bool json = false;
        bool quiet = false;
        bool sanitizePaths = false;
        var tokenOptions = new TokenOptions();

        var reader = new OptionReader(args);
        while (reader.MoveNext())
        {
            if (tokenOptions.TryConsume(reader))
            {
                continue;
            }
            switch (reader.Current)
            {
                case "--help" or "-h":
                    Console.Out.WriteLine(HelpText);
                    return 0;
                case "--org":
                    org = reader.RequireValue();
                    break;
                case "--target":
                    target = reader.RequireValue();
                    break;
                case "--parallel":
                    {
                        string value = reader.RequireValue();
                        if (!int.TryParse(value, out parallel) || parallel < 1)
                        {
                            throw new CliUsageException($"--parallel expects a positive integer, got '{value}'.");
                        }
                        break;
                    }
                case "--json":
                    reader.RejectValue();
                    json = true;
                    break;
                case "--quiet":
                    reader.RejectValue();
                    quiet = true;
                    break;
                case "--sanitize-paths":
                    reader.RejectValue();
                    sanitizePaths = true;
                    break;
                default:
                    throw new CliUsageException($"Unknown option '{reader.Current}' for 'gclo sync'.");
            }
        }

        if (string.IsNullOrWhiteSpace(org))
        {
            throw new CliUsageException("--org is required.");
        }
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new CliUsageException("--target is required.");
        }

        // One activity log per invocation. Only non-secret parameters are ever
        // written to it; the token is not.
        var log = new FileActivityLog();
        log.Info($"sync started: org='{org}', target='{target}', parallel={parallel}, "
            + $"sanitizePaths={sanitizePaths}, json={json}, quiet={quiet}");

        try
        {
            string token = tokenOptions.Resolve();

            var printer = new ProgressPrinter(printProgress: !quiet && !json, printFailures: !json);
            var git = new LibGit2GitClient();
            var engine = new OrgSyncEngine(new GitHubRepositoryLister(), git);
            var request = new SyncRequest(org.Trim(), token, target.Trim(), parallel);
            var progress = new SyncProgressHandler(printer, log, printDetails: !json, collectForSanitize: sanitizePaths);

            SyncSummary summary;
            try
            {
                summary = await engine
                    .SyncAsync(request, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Canceled while still listing repositories: nothing was processed yet.
                summary = new SyncSummary(0, 0, 0, 0, 0, WasCanceled: true);
            }
            catch (InvalidOperationException ex)
            {
                // The engine's listers translate auth failures, rate limiting, and
                // unknown organizations into InvalidOperationException.
                throw new CliErrorException(ex.Message, ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new CliErrorException($"Cannot use target folder '{target}': {ex.Message}", ex);
            }

            // --sanitize-paths: repositories whose only problem was Windows-invalid
            // paths were held back by the progress handler; retry each with an
            // automatic recovery. A recovered repository counts as cloned.
            int sanitized = 0;
            foreach (PendingRecovery pending in progress.DrainPendingRecoveries())
            {
                if (await TrySanitizeAsync(git, progress, log, target.Trim(), token, pending, cancellationToken)
                    .ConfigureAwait(false))
                {
                    sanitized++;
                }
            }
            if (sanitized > 0)
            {
                summary = summary with { Cloned = summary.Cloned + sanitized, Failed = summary.Failed - sanitized };
            }

            log.Info(
                $"sync finished: total={summary.Total}, cloned={summary.Cloned}, updated={summary.Updated}, "
                + $"failed={summary.Failed}, canceled={summary.Canceled}, wasCanceled={summary.WasCanceled}"
                + (sanitizePaths ? $", sanitizedPaths={sanitized}" : ""));

            if (json)
            {
                var result = new SyncJsonResult(
                    summary.Total, summary.Cloned, summary.Updated, summary.Failed,
                    summary.Canceled, summary.WasCanceled, printer.Failures);
                Console.Out.WriteLine(JsonSerializer.Serialize(result, CliJsonContext.Default.SyncJsonResult));
            }
            else
            {
                string verb = summary.WasCanceled ? "Canceled" : "Finished";
                string clonedText = sanitized > 0
                    ? $"{summary.Cloned} cloned ({sanitized} with sanitized paths)"
                    : $"{summary.Cloned} cloned";
                Console.Out.WriteLine(
                    $"{verb}: {clonedText}, {summary.Updated} updated, "
                    + $"{summary.Failed} failed, {summary.Canceled} canceled of {summary.Total}.");
            }

            return summary.Failed > 0 || summary.WasCanceled ? 1 : 0;
        }
        catch (Exception ex)
        {
            // Fatal path: missing or rejected token, unusable target folder,
            // unknown organization. Program prints the message; the log keeps it.
            log.Error($"sync failed: {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>
    /// Applies an automatic <see cref="PathRecovery"/> (rename to the suggested name,
    /// skip what has none) to one repository that failed with Windows-invalid paths.
    /// Reports the real outcome — Done or Failed — through <paramref name="progress"/>,
    /// which by now forwards to the console again.
    /// </summary>
    private static async Task<bool> TrySanitizeAsync(
        IGitClient git,
        SyncProgressHandler progress,
        IActivityLog log,
        string targetRoot,
        string token,
        PendingRecovery pending,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(targetRoot, pending.Repo);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PathRecovery recovery = BuildRecovery(pending.Paths);
            await git.ApplyRecoveryAsync(path, token, recovery, cancellationToken).ConfigureAwait(false);

            string detail = DescribeRecovery(recovery);
            Console.Error.WriteLine(
                $"{pending.Repo}  Sanitized  {recovery.SegmentRenames.Count} renamed, "
                + $"{recovery.SkippedPaths.Count} skipped: {detail}");
            log.Info($"{pending.Repo}: cloned with sanitized paths: {detail}");
            progress.Report(new RepoProgress(pending.Repo, SyncStatus.Done));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Canceled before this repo could be sanitized: surface the original failure.
            progress.Report(new RepoProgress(
                pending.Repo, SyncStatus.Failed, pending.Error, InvalidPaths: pending.Paths));
            return false;
        }
        catch (Exception ex)
        {
            progress.Report(new RepoProgress(
                pending.Repo, SyncStatus.Failed, $"path sanitization failed: {ex.Message}",
                InvalidPaths: (ex as InvalidRepositoryPathsException)?.Paths));
            return false;
        }
    }

    /// <summary>
    /// Builds the automatic recovery: each offending path is renamed to its suggested
    /// name; paths without one (e.g. case-only collisions, which a rename to the same
    /// suggestion could not resolve) are skipped entirely.
    /// </summary>
    private static PathRecovery BuildRecovery(IReadOnlyList<InvalidPathInfo> paths)
    {
        // Skip wins: a path that also has a suggestion-less problem is not materialized.
        var skipped = new HashSet<string>(
            paths.Where(p => string.IsNullOrEmpty(p.SuggestedName)).Select(p => p.RepoPath),
            StringComparer.Ordinal);

        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (InvalidPathInfo info in paths)
        {
            if (info.SuggestedName is not { Length: > 0 } suggestion || skipped.Contains(info.RepoPath))
            {
                continue;
            }
            // The suggestion replaces the last segment — the validator reports each
            // offending segment as a repo path ending in that segment.
            int slash = info.RepoPath.LastIndexOf('/');
            renames[info.RepoPath] = slash < 0 ? suggestion : info.RepoPath[..(slash + 1)] + suggestion;
        }
        return new PathRecovery(renames, skipped);
    }

    private static string DescribeRecovery(PathRecovery recovery)
        => string.Join("; ", recovery.SegmentRenames
            .Select(r => $"'{r.Key}' -> '{r.Value}'")
            .Concat(recovery.SkippedPaths.Select(s => $"skipped '{s}'")));

    /// <summary>One repository held back from failure reporting for a sanitization attempt.</summary>
    private sealed record PendingRecovery(string Repo, string Error, IReadOnlyList<InvalidPathInfo> Paths);

    /// <summary>
    /// Forwards every engine report to the console printer unchanged and mirrors
    /// failures into the activity log, so console output stays exactly what the
    /// printer alone would have produced — with two invalid-path additions: failure
    /// details (the offending paths, up to 10) follow the failure line on stderr,
    /// and with --sanitize-paths such failures are collected for a recovery attempt
    /// instead of being reported at all.
    /// </summary>
    private sealed class SyncProgressHandler : IProgress<RepoProgress>
    {
        private const int MaxListedPaths = 10;

        private readonly object _gate = new();
        private readonly ProgressPrinter _printer;
        private readonly IActivityLog _log;
        private readonly bool _printDetails;
        private readonly List<PendingRecovery> _pending = new();
        private bool _collectForSanitize;

        /// <param name="printDetails">Write offending-path lines to stderr (off for --json, which carries the reason in the document).</param>
        /// <param name="collectForSanitize">Hold invalid-path failures back for --sanitize-paths instead of reporting them.</param>
        public SyncProgressHandler(ProgressPrinter printer, IActivityLog log, bool printDetails, bool collectForSanitize)
        {
            _printer = printer;
            _log = log;
            _printDetails = printDetails;
            _collectForSanitize = collectForSanitize;
        }

        /// <summary>
        /// Returns the failures held back for sanitization and stops collecting:
        /// from here on Failed reports (the recovery attempts' outcomes) print normally.
        /// </summary>
        public IReadOnlyList<PendingRecovery> DrainPendingRecoveries()
        {
            lock (_gate)
            {
                _collectForSanitize = false;
                PendingRecovery[] drained = _pending.ToArray();
                _pending.Clear();
                return drained;
            }
        }

        // The engine reports from worker threads; the lock keeps a failure line and
        // its detail lines together.
        public void Report(RepoProgress value)
        {
            lock (_gate)
            {
                if (value.Status != SyncStatus.Failed)
                {
                    _printer.Report(value);
                    return;
                }

                if (_collectForSanitize && value.InvalidPaths is { Count: > 0 })
                {
                    _pending.Add(new PendingRecovery(
                        value.RepoName, value.Error ?? "invalid paths", value.InvalidPaths));
                    return;
                }

                _log.Error($"{value.RepoName} failed: {value.Error ?? "unknown error"}");
                _printer.Report(value);

                if (_printDetails && value.InvalidPaths is { Count: > 0 })
                {
                    // The WHY, for scripts: the failure message itself only carries
                    // the first three offending paths.
                    foreach (InvalidPathInfo info in value.InvalidPaths.Take(MaxListedPaths))
                    {
                        Console.Error.WriteLine($"{value.RepoName}    {info.RepoPath}  ({info.Reason})");
                    }
                    if (value.InvalidPaths.Count > MaxListedPaths)
                    {
                        Console.Error.WriteLine(
                            $"{value.RepoName}    ... and {value.InvalidPaths.Count - MaxListedPaths} more invalid paths");
                    }
                }
            }
        }
    }
}
