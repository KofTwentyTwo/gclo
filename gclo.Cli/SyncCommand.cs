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
        log.Info($"sync started: org='{org}', target='{target}', parallel={parallel}, json={json}, quiet={quiet}");

        try
        {
            string token = tokenOptions.Resolve();

            var printer = new ProgressPrinter(printProgress: !quiet && !json, printFailures: !json);
            var engine = new OrgSyncEngine(new GitHubRepositoryLister(), new LibGit2GitClient());
            var request = new SyncRequest(org.Trim(), token, target.Trim(), parallel);

            SyncSummary summary;
            try
            {
                summary = await engine
                    .SyncAsync(request, new FailureLoggingProgress(printer, log), cancellationToken)
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

            log.Info(
                $"sync finished: total={summary.Total}, cloned={summary.Cloned}, updated={summary.Updated}, "
                + $"failed={summary.Failed}, canceled={summary.Canceled}, wasCanceled={summary.WasCanceled}");

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
                Console.Out.WriteLine(
                    $"{verb}: {summary.Cloned} cloned, {summary.Updated} updated, "
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
    /// Forwards every engine report to the console printer unchanged and mirrors
    /// failures into the activity log, so console output stays exactly what the
    /// printer alone would have produced.
    /// </summary>
    private sealed class FailureLoggingProgress : IProgress<RepoProgress>
    {
        private readonly ProgressPrinter _printer;
        private readonly IActivityLog _log;

        public FailureLoggingProgress(ProgressPrinter printer, IActivityLog log)
        {
            _printer = printer;
            _log = log;
        }

        public void Report(RepoProgress value)
        {
            if (value.Status == SyncStatus.Failed)
            {
                _log.Error($"{value.RepoName} failed: {value.Error ?? "unknown error"}");
            }
            _printer.Report(value);
        }
    }
}
