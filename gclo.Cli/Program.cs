/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using gclo.Engine;


namespace gclo.Cli;


/// <summary>
/// Entry point: command dispatch, Ctrl+C wiring, and top-level error handling.
/// Excluded from coverage as the composition root — it wires the real network/native
/// collaborators (GitHub listers, LibGit2 client, Credential Manager) and the process
/// Console/Ctrl+C, none reproducible offline. The command logic it dispatches to lives
/// in the injectable internal cores, which are covered to 100%.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Composition root: wires real collaborators and the process Console/Ctrl+C.")]
internal static class Program
{
   private const string RootHelp = """
        gclo (Git Clone Large Organizations) - clone or update every repository of a GitHub organization or user account.

        Usage:
          gclo sync --org <name> --target <folder> [options]
          gclo sync --account <name> [options]
          gclo repos --org <name> [options]
          gclo orgs [options]
          gclo accounts [list|add|edit|remove] [options]
          gclo --version
          gclo --help

        Commands:
          sync      Clone every repository of an organization; fast-forward ones
                    that already exist locally. '--account <name>' runs it with a
                    saved account's settings and stored token. '--include',
                    '--exclude', and '--skip-archived' narrow the selection;
                    '--dry-run' shows it without syncing.
          repos     List the repositories a sync would see, with the same filters.
          orgs      List the account and organization logins the token can see.
          accounts  List, add, edit, or remove saved accounts (name, organization,
                    target root, parallelism, token in Windows Credential Manager).
                    Windows-only.

        Run 'gclo <command> --help' or 'gclo help <command>' for command options.

        Token:
          There is deliberately no '--token <value>' option: command-line arguments
          are visible to every other process on the machine (Task Manager, 'ps',
          WMI queries), so a token passed that way would leak. Provide it with:
            --token-env <VAR>    read it from environment variable VAR
            --token-file <path>  read the first non-blank line of a file
            --token-stdin        read one line from standard input
                                 (pipe it from a secret store)
          When no token option is given, the GITHUB_TOKEN environment variable is
          used — except with 'gclo sync --account', which reads the account's token
          from Windows Credential Manager instead. A token option always wins.

        Exit codes:
          0 success, 1 completed with failures or canceled, 2 usage or fatal error,
          3 token rejected or access denied, 4 rate limited (retry later),
          70 unexpected error (details in the activity log).
        """;



   public static async Task<int> Main(string[] args)
   {
      using var cts = new CancellationTokenSource();
      Console.CancelKeyPress += (_, e) =>
      {
         if(!cts.IsCancellationRequested)
         {
            // Finish gracefully: in-flight git operations stop, remaining
            // repositories are marked Canceled, and the summary still prints.
            e.Cancel = true;
            cts.Cancel();
            Console.Error.WriteLine("Canceling... (press Ctrl+C again to abort immediately)");
         }
         // Second Ctrl+C: e.Cancel stays false and the process terminates.
      };

      try
      {
         return await RunAsync(args, cts.Token).ConfigureAwait(false);
      }
      catch(CliUsageException ex)
      {
         Console.Error.WriteLine(ex.Message);
         Console.Error.WriteLine("Run 'gclo --help' for usage.");
         return ExitCodes.Fatal;
      }
      catch(CliErrorException ex)
      {
         Console.Error.WriteLine(ex.Message);
         return ex.ExitCode;
      }
      catch(OperationCanceledException) when(cts.IsCancellationRequested)
      {
         Console.Error.WriteLine("Canceled.");
         return ExitCodes.Partial;
      }
      catch(Exception ex)
      {
         return ReportUnexpected(ex);
      }
   }



   /// <summary>
   /// A bug in gclo, not a caller mistake: say so, with the exception type, and point
   /// at the activity log where the full exception (with its stack) is recorded, so
   /// a bug report has something to go on. Exit 70 keeps it apart from exit 2.
   /// </summary>
   private static int ReportUnexpected(Exception ex)
   {
      string details = "";
      try
      {
         var log = new FileActivityLog();
         log.Error($"unexpected error ({ex.GetType().FullName}): {ex.Message}", ex);
         details = log.CurrentLogFilePath;
      }
      catch
      {
         // The log must never make a bad situation worse.
      }

      Console.Error.WriteLine($"Unexpected error ({ex.GetType().Name}): {ex.Message}");
      if(details.Length > 0)
      {
         Console.Error.WriteLine($"Details: {details}");
      }
      Console.Error.WriteLine("This looks like a bug in gclo; please report it at https://github.com/KofTwentyTwo/gclo/issues.");
      return ExitCodes.Unexpected;
   }



   private static Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
   {
      if(args.Length == 0)
      {
         throw new CliUsageException("No command given.");
      }

      string command = args[0];
      string[] rest = args[1..];
      switch(command)
      {
         case "sync":
            return SyncCommand.RunAsync(rest, cancellationToken);
         case "repos":
            return ReposCommand.RunAsync(rest, cancellationToken);
         case "orgs":
            return OrgsCommand.RunAsync(rest, cancellationToken);
         case "accounts":
            return Task.FromResult(AccountsCommand.Run(rest));
         case "--help" or "-h":
            Console.Out.WriteLine(RootHelp);
            return Task.FromResult(ExitCodes.Success);
         case "help":
            if(rest.Length == 0)
            {
               Console.Out.WriteLine(RootHelp);
               return Task.FromResult(ExitCodes.Success);
            }
            // 'gclo help sync' means 'gclo sync --help'; an unknown name is a usage error.
            return rest[0] is "sync" or "repos" or "orgs" or "accounts"
                ? RunAsync([rest[0], "--help"], cancellationToken)
                : throw new CliUsageException($"Unknown command '{rest[0]}'.");
         case "--version":
            Console.Out.WriteLine(Version());
            return Task.FromResult(ExitCodes.Success);
         default:
            throw new CliUsageException($"Unknown command '{command}'.");
      }
   }



   private static string Version()
       // Shared with the GUI's About dialog: "0.1.0-beta.6 (9ac6c2b)" for
       // releases, "0.1.0-dev (<hash>)" for local builds.
       => BuildVersion.Describe(typeof(Program).Assembly);
}
