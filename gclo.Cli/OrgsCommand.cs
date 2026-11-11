/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using gclo.Engine;


namespace gclo.Cli;


/// <summary>'gclo orgs': list the account and organization logins a token can see.</summary>
internal static class OrgsCommand
{
   private const string HelpText = """
        Usage: gclo orgs [options]

        Lists the logins the token can sync, one per line: the token's own account
        first, then its organizations alphabetically. A token that cannot list
        organizations (GitHub: a fine-grained PAT, or a classic PAT without read:org;
        Gitea: a token without read:organization) still prints its own account login.

        Options:
          --forge <kind>       Where the organization lives: github (the default) or
                               gitea (a Gitea or Forgejo server; needs --server).
          --server <address>   The address of the Gitea server, for example
                               https://git.example.com. HTTPS only: it is where the
                               token is sent. Implies --forge gitea.
          --token-env <VAR>    Read the token from environment variable VAR
                               (default when no token option is given: GITHUB_TOKEN,
                               or GITEA_TOKEN for a Gitea server; never the other's).
          --token-file <path>  Read the token from the first non-blank line of a file.
          --token-stdin        Read the token as one line from standard input.
          --json               Print a single-line JSON array instead.
          --help               Show this help.

        Exit codes:
          0  listed successfully (even when only the account login is visible)
          1  canceled (Ctrl+C)
          2  fatal: bad arguments, or missing token
          3  the token was rejected (401) or refused access (403)
          4  rate limit, network failure, timeout, or an error on the server's side; retry later

        Security:
          There is deliberately no '--token <value>' option: process command lines
          are visible to other processes on the machine, so a token passed as an
          argument would leak. Use --token-env, --token-file, or --token-stdin.
        """;



   /// <summary>Composition root: wires the real listers of the forge the options name, and the file log.</summary>
   [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(
       Justification = "Wires the real network lister and file log; delegates to the covered core.")]
   public static Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
       => RunAsync(
           args, endpoint => endpoint.CreateOrganizationLister(), new FileActivityLog(gclo.ViewModels.GcloPaths.LogDirectory), cancellationToken);



   /// <summary>The command with one lister, whatever forge the options name.</summary>
   internal static Task<int> RunAsync(
       string[] args, IOrganizationLister lister, IActivityLog log, CancellationToken cancellationToken)
       => RunAsync(args, _ => lister, log, cancellationToken);



   internal static async Task<int> RunAsync(
       string[] args, Func<ForgeEndpoint, IOrganizationLister> listers, IActivityLog log, CancellationToken cancellationToken)
   {
      bool json = false;
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
            case "--json":
               reader.RejectValue();
               json = true;
               break;
            default:
               throw new CliUsageException($"Unknown option '{reader.Current}' for 'gclo orgs'.");
         }
      }

      ForgeEndpoint endpoint = forgeOptions.Resolve();
      log.Info($"orgs started: forge={endpoint}, json={json}");

      try
      {
         string token = tokenOptions.Resolve(endpoint.Kind);

         IReadOnlyList<string> logins;
         try
         {
            logins = await listers(endpoint)
                .ListOrganizationsAsync(token, cancellationToken)
                .ConfigureAwait(false);
         }
         catch(InvalidOperationException ex)
         {
            // The lister translates auth failures and rate limiting; the kind
            // picks the exit code (2/3/4).
            throw CliErrorException.FromEngine(ex);
         }

         log.Info($"orgs finished: {logins.Count} login(s) listed.");

         if(json)
         {
            Terminal.OutVerbatim(JsonSerializer.Serialize(logins, CliJsonContext.Default.IReadOnlyListString));
         }
         else
         {
            foreach(string login in logins)
            {
               Terminal.Out(login);
            }
         }
         return ExitCodes.Success;
      }
      catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
      {
         log.Info("orgs canceled.");
         throw;
      }
      catch(Exception ex)
      {
         // Fatal path: missing or rejected token. Program prints the message;
         // the log keeps it.
         log.Error($"orgs failed: {ex.Message}", ex);
         throw;
      }
   }
}
