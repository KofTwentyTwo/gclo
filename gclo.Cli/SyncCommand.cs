/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using gclo.Engine;
using gclo.ViewModels;


namespace gclo.Cli;


/// <summary>'gclo sync': clone or fast-forward every repository of an organization.</summary>
internal static class SyncCommand
{
   private const string HelpText = """
        Usage: gclo sync --org <name> --target <folder> [options]
               gclo sync --account <name> [options]

        Clones every repository of <name> into <folder>\<repo>. Repositories that
        already exist locally are fetched and fast-forwarded instead. Repositories
        fail independently; one failure never stops the rest.

        Options:
          --org <name>         GitHub organization or user account to sync
                               (required unless --account provides it).
          --target <folder>    Local root folder; each repository becomes a subfolder
                               (required unless --account provides it; created if
                               missing).
          --account <name>     Use a saved account (see 'gclo accounts'; Windows
                               only): seeds --org, --target, and --parallel from
                               the account and reads the token from Windows
                               Credential Manager. When the account opts into an
                               organization subfolder the target becomes
                               <targetRoot>\<org>. Explicit options override the
                               account's values; a token option overrides the
                               stored token. On completion the run's time and
                               summary are recorded on the account.
          --include <glob>     Only repositories whose name matches the pattern
                               ('*' and '?' wildcards, case-insensitive). Repeatable;
                               a repository is selected when any include matches.
          --exclude <glob>     Skip repositories whose name matches. Repeatable;
                               applied after --include.
          --skip-archived      Skip archived repositories.
          --dry-run            List what the run would do ('<name>  would clone' or
                               'would update') and exit 0 without touching anything.
          --parallel <N>       Maximum simultaneous git operations, 1-64 (default 8).
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
          --token-file <path>  Read the token from the first non-blank line of a file.
          --token-stdin        Read the token as one line from standard input.
          --json               Print no progress lines; end with one line of JSON:
                               {"type":"summary","total":N,"cloned":N,"updated":N,
                                "failed":N,"canceled":N,"wasCanceled":bool,
                                "failures":[{"repo":"...","error":"...",
                                  "invalidPaths":null|[{"path","reason","suggestedName"}]}],
                                "sanitized":[{"repo":"...","renamed":N,"skipped":N,
                                  "skippedPaths":["..."]}]}
          --json-lines         Print one JSON object per status transition on stdout
                               ({"type":"progress","repo","status","error"}), then the
                               same summary object as --json as the last line.
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
          0   every selected repository synced (or --dry-run completed)
          1   run completed but some repositories failed, or it was canceled
          2   fatal: bad arguments, missing token, organization not found, unknown
              account or missing account token, unusable target folder
          3   the token was rejected (401) or may not see the organization (403):
              fix the token, do not retry as is
          4   rate limited or temporarily throttled: retry later
          70  an unexpected error inside gclo; see the activity log
          With --json or --json-lines, exits 2-70 print no document on stdout.

        Security:
          There is deliberately no '--token <value>' option: process command lines
          are visible to other processes on the machine, so a token passed as an
          argument would leak. Use --token-env, --token-file, or --token-stdin.
        """;



   /// <summary>Everything `gclo sync` reads from its command line, before any work starts.</summary>
   private sealed class SyncOptions
   {
      public string? Org { get; set; }

      public string? Target { get; set; }

      public string? AccountName { get; set; }

      public int? Parallel { get; set; }

      public bool Json { get; set; }

      public bool JsonLines { get; set; }

      public bool Quiet { get; set; }

      public bool SanitizePaths { get; set; }

      public bool DryRun { get; set; }

      public RepoFilterSpec Filter { get; } = new();

      public TokenOptions TokenOptions { get; } = new();
   }



   /// <summary>
   /// Parses the sync options; null when --help was asked for (the help text has
   /// been printed). Usage mistakes throw <see cref="CliUsageException"/>.
   /// </summary>
   private static SyncOptions? ParseOptions(string[] args)
   {
      var options = new SyncOptions();
      var reader = new OptionReader(args);
      while(reader.MoveNext())
      {
         if(options.TokenOptions.TryConsume(reader))
         {
            continue;
         }
         switch(reader.Current)
         {
            case "--help" or "-h":
               Console.Out.WriteLine(HelpText);
               return null;
            case "--org":
               options.Org = reader.RequireValue();
               break;
            case "--target":
               options.Target = reader.RequireValue();
               break;
            case "--account":
               options.AccountName = reader.RequireValue();
               break;
            case "--include":
               options.Filter.Include(reader.RequireValue());
               break;
            case "--exclude":
               options.Filter.Exclude(reader.RequireValue());
               break;
            case "--skip-archived":
               reader.RejectValue();
               options.Filter.SkipArchived = true;
               break;
            case "--dry-run":
               reader.RejectValue();
               options.DryRun = true;
               break;
            case "--parallel":
               options.Parallel = OptionReader.ParseParallel(reader.RequireValue());
               break;
            case "--json":
               reader.RejectValue();
               options.Json = true;
               break;
            case "--json-lines":
               reader.RejectValue();
               options.JsonLines = true;
               break;
            case "--quiet":
               reader.RejectValue();
               options.Quiet = true;
               break;
            case "--sanitize-paths":
               reader.RejectValue();
               options.SanitizePaths = true;
               break;
            default:
               throw new CliUsageException($"Unknown option '{reader.Current}' for 'gclo sync'.");
         }
      }

      if(options.Json && options.JsonLines)
      {
         throw new CliUsageException("Use --json or --json-lines, not both.");
      }
      return options;
   }



   /// <summary>
   /// Prints the run's summary (one JSON document, or the human line) and records it
   /// on the saved account when the run came from one.
   /// </summary>
   private static void ReportSummary(
       SyncSummary summary,
       int sanitized,
       ProgressPrinter printer,
       bool machineReadable,
       AccountsStore? store,
       Account? account,
       IActivityLog log)
   {
      string verb = summary.WasCanceled ? "Canceled" : "Finished";
      string clonedText = sanitized > 0
          ? $"{summary.Cloned} cloned ({sanitized} with sanitized paths)"
          : $"{summary.Cloned} cloned";
      string summaryLine = $"{verb}: {clonedText}, {summary.Updated} updated, "
          + $"{summary.Failed} failed, {summary.Canceled} canceled of {summary.Total}.";

      if(machineReadable)
      {
         var result = new SyncJsonResult(
             "summary", summary.Total, summary.Cloned, summary.Updated, summary.Failed,
             summary.Canceled, summary.WasCanceled, printer.Failures, printer.Sanitized);
         Console.Out.WriteLine(JsonSerializer.Serialize(result, CliJsonContext.Default.SyncJsonResult));
      }
      else
      {
         Console.Out.WriteLine(summaryLine);
      }

      if(account is not null)
      {
         // The run completed (exit 0 or 1): remember when and how it went,
         // so 'gclo accounts' and the desktop app can show it.
         TryRecordSyncResult(store!, account, summaryLine, log);
      }
   }



   /// <summary>Composition root: wires the real GitHub lister, LibGit2 client, and file log.</summary>
   [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(
       Justification = "Wires the real network/native collaborators; delegates to the covered core.")]
   public static Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
       => RunAsync(
           args,
           new GitHubRepositoryLister(), new LibGit2GitClient(), AccountsCommand.Open, new FileActivityLog(),
           cancellationToken);



   internal static async Task<int> RunAsync(
       string[] args,
       IRepositoryLister lister,
       IGitClient git,
       Func<IActivityLog, (AccountsStore Store, ITokenVault Vault)> openAccounts,
       IActivityLog log,
       CancellationToken cancellationToken)
   {
      SyncOptions? options = ParseOptions(args);
      if(options is null)
      {
         return ExitCodes.Success; // --help was printed
      }
      string? org = options.Org;
      string? target = options.Target;
      string? accountName = options.AccountName;
      int? parallel = options.Parallel;
      bool json = options.Json;
      bool jsonLines = options.JsonLines;
      bool quiet = options.Quiet;
      bool sanitizePaths = options.SanitizePaths;
      bool dryRun = options.DryRun;
      RepoFilterSpec filter = options.Filter;
      TokenOptions tokenOptions = options.TokenOptions;
      bool machineReadable = json || jsonLines;

      try
      {
         // --account: seed org/target/parallel from the saved account. Explicit
         // options given alongside win because ??= only fills what is still unset.
         Account? account = null;
         AccountsStore? store = null;
         ITokenVault? vault = null;
         if(accountName is not null)
         {
            (store, vault) = openAccounts(log);
            account = store.FindByName(accountName) ?? throw UnknownAccount(accountName, store);
            org ??= account.Organization;
            target ??= account.CreateOrgSubfolder ? Path.Combine(account.TargetRoot, org) : account.TargetRoot;
            parallel ??= account.MaxConcurrency;
         }

         if(string.IsNullOrWhiteSpace(org))
         {
            throw new CliUsageException("--org is required (or use --account).");
         }
         if(string.IsNullOrWhiteSpace(target))
         {
            throw new CliUsageException("--target is required (or use --account).");
         }
         int effectiveParallel = parallel ?? AppSettings.DefaultConcurrency;
         string organization = org.Trim();
         string targetRoot = target.Trim();

         log.Info($"sync started: org='{organization}', target='{targetRoot}', parallel={effectiveParallel}, "
             + $"filters={filter}, dryRun={dryRun}, sanitizePaths={sanitizePaths}, json={json}, jsonLines={jsonLines}, quiet={quiet}"
             + (account is null ? "" : $", account='{account.Name}'"));

         string token = ResolveToken(tokenOptions, account, vault);

         // List first (the desktop app's list-select-sync shape): filters and
         // --dry-run need the descriptors before anything runs, and the engine's
         // subset overload takes exactly that list.
         IReadOnlyList<RepoDescriptor> repositories;
         bool canceledWhileListing = false;
         try
         {
            repositories = await lister
                .ListOrganizationRepositoriesAsync(organization, token, cancellationToken)
                .ConfigureAwait(false);
         }
         catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
         {
            repositories = [];
            canceledWhileListing = true;
         }
         catch(InvalidOperationException ex)
         {
            // The listers translate auth failures, rate limiting, and unknown
            // organizations; the kind decides the exit code (2/3/4).
            throw CliErrorException.FromEngine(ex);
         }

         int listed = repositories.Count;
         if(!filter.IsEmpty)
         {
            repositories = repositories.Where(filter.Matches).ToList();
         }
         log.Info($"sync: {listed} repositories listed, {repositories.Count} selected.");

         if(dryRun)
         {
            return PrintDryRun(repositories, git, targetRoot, machineReadable, log);
         }

         OutputMode mode = jsonLines ? OutputMode.JsonLines
             : json ? OutputMode.Json
             : quiet ? OutputMode.Quiet
             : OutputMode.Text;
         var printer = new ProgressPrinter(mode);
         var engine = new OrgSyncEngine(lister, git);
         var request = new SyncRequest(organization, token, targetRoot, effectiveParallel);
         var progress = new SyncProgressHandler(printer, log, printDetails: !machineReadable, collectForSanitize: sanitizePaths);

         SyncSummary summary;
         if(canceledWhileListing)
         {
            // Canceled while still listing repositories: nothing was processed yet.
            summary = new SyncSummary(0, 0, 0, 0, 0, WasCanceled: true);
         }
         else
         {
            try
            {
               summary = await engine
                   .SyncAsync(request, repositories, progress, cancellationToken)
                   .ConfigureAwait(false);
            }
            // The engine folds cancellation into the summary itself (remaining
            // repositories are marked Canceled), so only disk failures surface here.
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
               throw new CliErrorException($"Cannot use target folder '{targetRoot}': {ex.Message}", ex);
            }
         }

         // --sanitize-paths: repositories whose only problem was Windows-invalid
         // paths were held back by the progress handler; retry each with an
         // automatic recovery. A recovered repository counts as cloned.
         int sanitized = 0;
         foreach(PendingRecovery pending in progress.DrainPendingRecoveries())
         {
            if(await TrySanitizeAsync(git, progress, printer, log, targetRoot, pending, announce: !machineReadable, cancellationToken)
                .ConfigureAwait(false))
            {
               sanitized++;
            }
         }
         if(sanitized > 0)
         {
            summary = summary with { Cloned = summary.Cloned + sanitized, Failed = summary.Failed - sanitized };
         }

         log.Info(
             $"sync finished: total={summary.Total}, cloned={summary.Cloned}, updated={summary.Updated}, "
             + $"failed={summary.Failed}, canceled={summary.Canceled}, wasCanceled={summary.WasCanceled}"
             + (sanitizePaths ? $", sanitizedPaths={sanitized}" : ""));

         ReportSummary(summary, sanitized, printer, machineReadable, store, account, log);

         return summary.Failed > 0 || summary.WasCanceled ? ExitCodes.Partial : ExitCodes.Success;
      }
      catch(Exception ex)
      {
         // Fatal path: missing or rejected token, unusable target folder,
         // unknown organization. Program prints the message; the log keeps it.
         log.Error($"sync failed: {ex.Message}", ex);
         throw;
      }
   }



   /// <summary>
   /// '--dry-run': says what the run would do with each selected repository and
   /// touches nothing. Exit 0 even for an empty selection.
   /// </summary>
   private static int PrintDryRun(
       IReadOnlyList<RepoDescriptor> repositories, IGitClient git, string targetRoot, bool json, IActivityLog log)
   {
      var entries = repositories
          .Select(r => new DryRunEntry(
              r.Name,
              git.IsValidRepository(Path.Combine(targetRoot, r.Name)) ? "update" : "clone"))
          .ToList();
      int clones = entries.Count(e => string.Equals(e.Action, "clone", StringComparison.Ordinal));
      log.Info($"sync dry run: {clones} would clone, {entries.Count - clones} would update of {entries.Count}.");

      if(json)
      {
         Console.Out.WriteLine(JsonSerializer.Serialize(
             (IReadOnlyList<DryRunEntry>)entries, CliJsonContext.Default.IReadOnlyListDryRunEntry));
         return ExitCodes.Success;
      }

      foreach(DryRunEntry entry in entries)
      {
         Console.Out.WriteLine($"{entry.Repo}  would {entry.Action}");
      }
      Console.Out.WriteLine($"Dry run: {clones} would clone, {entries.Count - clones} would update of {entries.Count}.");
      return ExitCodes.Success;
   }



   /// <summary>
   /// The token for this run. Any explicit token option wins; otherwise a saved
   /// account's token comes from Windows Credential Manager; plain runs fall back
   /// to the default environment variable.
   /// </summary>
   private static string ResolveToken(TokenOptions tokenOptions, Account? account, ITokenVault? vault)
   {
      if(account is null || tokenOptions.HasExplicitSource)
      {
         return tokenOptions.Resolve();
      }

      // vault is non-null whenever account is: both come from AccountsCommand.Open.
      string? token = vault!.TryRetrieve(account.Id);
      if(string.IsNullOrEmpty(token))
      {
         throw new CliErrorException(
             $"Account '{account.Name}' has no token in Windows Credential Manager "
             + $"(entry 'gclo:account:{account.Id:N}'). Re-enter the token with "
             + "'gclo accounts edit --name <name> --token-stdin' or in the desktop app's account wizard, "
             + "restore the credential entry manually, or pass a token with --token-env, --token-file, or --token-stdin.");
      }
      return token;
   }



   /// <summary>
   /// Records the completed run's time and summary on the account. A persistence
   /// failure must not change the sync's exit code — the repositories are already
   /// on disk — so it is reported as a warning instead of propagating.
   /// </summary>
   private static void TryRecordSyncResult(AccountsStore store, Account account, string summaryLine, IActivityLog log)
   {
      try
      {
         store.RecordSyncResult(account.Id, DateTimeOffset.UtcNow, summaryLine);
      }
      catch(Exception ex)
      {
         log.Error($"could not record the sync result on account '{account.Name}': {ex.Message}", ex);
         Console.Error.WriteLine(
             $"Warning: could not record the sync result on account '{account.Name}': {ex.Message}");
      }
   }



   /// <summary>Unknown --account name: tell the user what does exist (exit code 2).</summary>
   private static CliErrorException UnknownAccount(string name, AccountsStore store)
   {
      IReadOnlyList<Account> all = store.GetAll();
      string available = all.Count == 0
          ? "No accounts exist yet; create one with 'gclo accounts add' or in the gclo desktop app."
          : "Available accounts: " + string.Join(", ", all.Select(a => a.Name)) + ".";
      return new CliErrorException($"No account named '{name}'. {available}");
   }



   /// <summary>
   /// Applies an automatic <see cref="PathRecovery"/> (rename to the suggested name,
   /// skip what has none) to one repository that failed with Windows-invalid paths.
   /// Reports the real outcome — Done or Failed — through <paramref name="progress"/>,
   /// which by now forwards to the console again, and records the sanitized shape
   /// for the JSON document. The human-readable note is only printed in the text
   /// modes (<paramref name="announce"/>); the document carries it otherwise.
   /// </summary>
   private static async Task<bool> TrySanitizeAsync(
       IGitClient git,
       SyncProgressHandler progress,
       ProgressPrinter printer,
       IActivityLog log,
       string targetRoot,
       PendingRecovery pending,
       bool announce,
       CancellationToken cancellationToken)
   {
      string path = Path.Combine(targetRoot, pending.Repo);
      try
      {
         cancellationToken.ThrowIfCancellationRequested();
         PathRecovery recovery = BuildRecovery(pending.Paths);
         await git.ApplyRecoveryAsync(path, recovery, cancellationToken).ConfigureAwait(false);

         string detail = DescribeRecovery(recovery);
         if(announce)
         {
            Console.Error.WriteLine(
                $"{pending.Repo}  Sanitized  {recovery.SegmentRenames.Count} renamed, "
                + $"{recovery.SkippedPaths.Count} skipped: {detail}");
         }
         log.Info($"{pending.Repo}: cloned with sanitized paths: {detail}");
         printer.RecordSanitized(new SyncSanitized(
             pending.Repo,
             recovery.SegmentRenames.Count,
             recovery.SkippedPaths.Count,
             recovery.SkippedPaths.Order(StringComparer.Ordinal).ToList()));
         progress.Report(new RepoProgress(pending.Repo, SyncStatus.Done));
         return true;
      }
      catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
      {
         // Canceled before this repo could be sanitized: surface the original failure.
         progress.Report(new RepoProgress(
             pending.Repo, SyncStatus.Failed, pending.Error, InvalidPaths: pending.Paths));
         return false;
      }
      catch(Exception ex)
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
      foreach(InvalidPathInfo info in paths)
      {
         if(info.SuggestedName is not { Length: > 0 } suggestion || skipped.Contains(info.RepoPath))
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

      private readonly Lock _gate = new();

      private readonly ProgressPrinter _printer;

      private readonly IActivityLog _log;

      private readonly bool _printDetails;

      private readonly List<PendingRecovery> _pending = new();

      private bool _collectForSanitize;



      /// <param name="printer"></param>

      /// <param name="log"></param>      /// <param name="printDetails">Write offending-path lines to stderr (off for the JSON modes, whose document carries the full list).</param>
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
         lock(_gate)
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
         lock(_gate)
         {
            if(value.Status != SyncStatus.Failed)
            {
               _printer.Report(value);
               return;
            }

            if(_collectForSanitize && value.InvalidPaths is { Count: > 0 })
            {
               _pending.Add(new PendingRecovery(
                   value.RepoName, value.Error ?? "invalid paths", value.InvalidPaths));
               return;
            }

            _log.Error($"{value.RepoName} failed: {value.Error ?? "unknown error"}");
            _printer.Report(value);

            if(_printDetails && value.InvalidPaths is { Count: > 0 })
            {
               // The WHY, for scripts: the failure message itself only carries
               // the first three offending paths.
               foreach(InvalidPathInfo info in value.InvalidPaths.Take(MaxListedPaths))
               {
                  Console.Error.WriteLine($"{value.RepoName}    {info.RepoPath}  ({info.Reason})");
               }
               if(value.InvalidPaths.Count > MaxListedPaths)
               {
                  Console.Error.WriteLine(
                      $"{value.RepoName}    ... and {value.InvalidPaths.Count - MaxListedPaths} more invalid paths");
               }
            }
         }
      }
   }
}
