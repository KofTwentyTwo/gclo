/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using gclo.Engine;


namespace gclo.Cli;


/// <summary>'gclo repos': list the repositories a sync would see, with the same filters.</summary>
internal static class ReposCommand
{
   private const string HelpText = """
        Usage: gclo repos --org <name> [options]

        Lists the repositories of <name> that 'gclo sync' would process — one per
        line: name, default branch ('-' for an empty repository), and 'archived'
        when the repository is archived. Sorted by name. Use it to preview a
        selection before syncing, or to feed a script.

        Options:
          --org <name>         Organization or user account (required).
          --forge <kind>       Where the organization lives: github (the default) or
                               gitea (a Gitea or Forgejo server; needs --server).
          --server <address>   The address of the Gitea server, for example
                               https://git.example.com. HTTPS only: it is where the
                               token is sent. Implies --forge gitea.
          --include <glob>     Only repositories whose name matches the pattern
                               ('*' and '?' wildcards, case-insensitive). Repeatable;
                               a repository matches when any include matches.
          --exclude <glob>     Drop repositories whose name matches. Repeatable;
                               applied after --include.
          --skip-archived      Drop archived repositories.
          --token-env <VAR>    Read the token from environment variable VAR
                               (default when no token option is given: GITHUB_TOKEN,
                               or GITEA_TOKEN for a Gitea server; never the other's).
          --token-file <path>  Read the token from the first non-blank line of a file.
          --token-stdin        Read the token as one line from standard input.
          --json               Print a single-line JSON array instead:
                               [{"name":"...","defaultBranch":"main"|null,"archived":bool,"cloneUrl":"..."}]
          --help               Show this help.

        Exit codes:
          0  listed successfully (also when the selection is empty)
          1  canceled (Ctrl+C)
          2  fatal: bad arguments, missing token, organization not found
          3  the token was rejected or may not see the organization
          4  rate limit, network failure, timeout, or an error on the server's side; retry later
        """;



   /// <summary>Composition root: wires the real listers of the forge the options name, and the file log.</summary>
   [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(
       Justification = "Wires the real network lister and file log; delegates to the covered core.")]
   public static Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
       => RunAsync(
           args, endpoint => endpoint.CreateRepositoryLister(), new FileActivityLog(gclo.ViewModels.GcloPaths.LogDirectory), cancellationToken);



   /// <summary>The command with one lister, whatever forge the options name.</summary>
   internal static Task<int> RunAsync(
       string[] args, IRepositoryLister lister, IActivityLog log, CancellationToken cancellationToken)
       => RunAsync(args, _ => lister, log, cancellationToken);



   internal static async Task<int> RunAsync(
       string[] args, Func<ForgeEndpoint, IRepositoryLister> listers, IActivityLog log, CancellationToken cancellationToken)
   {
      string? org = null;
      bool json = false;
      var filter = new RepoFilterSpec();
      var tokenOptions = new TokenOptions();
      var forgeOptions = new ForgeOptions();

      var reader = new OptionReader(args);
      while(reader.MoveNext())
      {
         if(tokenOptions.TryConsume(reader) || forgeOptions.TryConsume(reader))
         {
            continue;
         }
         switch(reader.Current)
         {
            case "--help" or "-h":
               Terminal.OutVerbatim(HelpText);
               return ExitCodes.Success;
            case "--org":
               org = reader.RequireValue();
               break;
            case "--include":
               filter.Include(reader.RequireValue());
               break;
            case "--exclude":
               filter.Exclude(reader.RequireValue());
               break;
            case "--skip-archived":
               reader.RejectValue();
               filter.SkipArchived = true;
               break;
            case "--json":
               reader.RejectValue();
               json = true;
               break;
            default:
               throw new CliUsageException($"Unknown option '{reader.Current}' for 'gclo repos'.");
         }
      }

      if(string.IsNullOrWhiteSpace(org))
      {
         throw new CliUsageException("--org is required.");
      }

      ForgeEndpoint endpoint = forgeOptions.Resolve();
      log.Info($"repos started: org='{org}', forge={endpoint}, filters={filter}, json={json}");

      try
      {
         string token = tokenOptions.Resolve(endpoint.Kind);

         IReadOnlyList<RepoDescriptor> repositories;
         try
         {
            repositories = await listers(endpoint)
                .ListOrganizationRepositoriesAsync(org.Trim(), token, cancellationToken)
                .ConfigureAwait(false);
         }
         catch(InvalidOperationException ex)
         {
            throw CliErrorException.FromEngine(ex);
         }

         int listed = repositories.Count;
         if(!filter.IsEmpty)
         {
            repositories = repositories.Where(filter.Matches).ToList();
         }
         log.Info($"repos finished: {listed} listed, {repositories.Count} selected.");

         if(json)
         {
            IReadOnlyList<RepoSummary> summaries = repositories
                .Select(r => new RepoSummary(r.Name, r.DefaultBranch, r.IsArchived, r.CloneUrl))
                .ToList();
            Terminal.OutVerbatim(JsonSerializer.Serialize(summaries, CliJsonContext.Default.IReadOnlyListRepoSummary));
            return ExitCodes.Success;
         }

         if(repositories.Count == 0)
         {
            Terminal.Error(listed == 0
                ? $"No repositories visible in '{org}'."
                : $"No repositories of '{org}' match the filters ({listed} listed).");
            return ExitCodes.Success;
         }

         int nameWidth = repositories.Max(r => r.Name.Length);
         int branchWidth = repositories.Max(r => (r.DefaultBranch ?? "-").Length);
         foreach(RepoDescriptor repo in repositories)
         {
            string branch = (repo.DefaultBranch ?? "-").PadRight(branchWidth);
            Terminal.Out(
                $"{repo.Name.PadRight(nameWidth)}  {branch}" + (repo.IsArchived ? "  archived" : ""));
         }
         return ExitCodes.Success;
      }
      catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
      {
         log.Info("repos canceled.");
         throw;
      }
      catch(Exception ex)
      {
         log.Error($"repos failed: {ex.Message}", ex);
         throw;
      }
   }
}
