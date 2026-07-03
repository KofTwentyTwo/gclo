using System.Reflection;

namespace gclo.Cli;

/// <summary>Entry point: command dispatch, Ctrl+C wiring, and top-level error handling.</summary>
internal static class Program
{
    private const string RootHelp = """
        gclo (Git Clone Large Organizations) - clone or update every repository of a GitHub organization or user account.

        Usage:
          gclo sync --org <name> --target <folder> [options]
          gclo orgs [options]
          gclo --version
          gclo --help

        Commands:
          sync   Clone every repository of an organization; fast-forward ones that
                 already exist locally.
          orgs   List the account and organization logins the token can see.

        Run 'gclo sync --help' or 'gclo orgs --help' for command options.

        Token:
          There is deliberately no '--token <value>' option: command-line arguments
          are visible to every other process on the machine (Task Manager, 'ps',
          WMI queries), so a token passed that way would leak. Provide it with:
            --token-env <VAR>    read it from environment variable VAR
            --token-file <path>  read the first non-blank line of a file
            --token-stdin        read one line from standard input
                                 (pipe it from a secret store)
          When no token option is given, the GITHUB_TOKEN environment variable is used.
        """;

    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            if (!cts.IsCancellationRequested)
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
        catch (CliUsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Run 'gclo --help' for usage.");
            return 2;
        }
        catch (CliErrorException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine("Canceled.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            throw new CliUsageException("No command given.");
        }

        string command = args[0];
        string[] rest = args[1..];
        switch (command)
        {
            case "sync":
                return SyncCommand.RunAsync(rest, cancellationToken);
            case "orgs":
                return OrgsCommand.RunAsync(rest, cancellationToken);
            case "--help" or "-h" or "help":
                Console.Out.WriteLine(RootHelp);
                return Task.FromResult(0);
            case "--version":
                Console.Out.WriteLine(Version());
                return Task.FromResult(0);
            default:
                throw new CliUsageException($"Unknown command '{command}'.");
        }
    }

    private static string Version()
    {
        Assembly assembly = typeof(Program).Assembly;
        string version =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        // Strip the '+<metadata>' suffix (e.g. a SourceLink commit hash); it is
        // noise for a scripted version check.
        int plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }
}
