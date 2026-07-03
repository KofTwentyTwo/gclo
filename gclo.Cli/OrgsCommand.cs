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
        organizations (fine-grained PAT, or a classic PAT without read:org) still
        prints its own account login.

        Options:
          --token-env <VAR>    Read the token from environment variable VAR
                               (default: GITHUB_TOKEN when no token option is given).
          --token-file <path>  Read the token from the first line of a file.
          --token-stdin        Read the token as one line from standard input.
          --json               Print a single-line JSON array instead.
          --help               Show this help.

        Exit codes:
          0  listed successfully (even when only the account login is visible)
          1  canceled (Ctrl+C)
          2  fatal: bad arguments, or missing or rejected token

        Security:
          There is deliberately no '--token <value>' option: process command lines
          are visible to other processes on the machine, so a token passed as an
          argument would leak. Use --token-env, --token-file, or --token-stdin.
        """;

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        bool json = false;
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
                case "--json":
                    reader.RejectValue();
                    json = true;
                    break;
                default:
                    throw new CliUsageException($"Unknown option '{reader.Current}' for 'gclo orgs'.");
            }
        }

        // One activity log per invocation; the token itself is never written to it.
        var log = new FileActivityLog();
        log.Info($"orgs started: json={json}");

        try
        {
            string token = tokenOptions.Resolve();

            IReadOnlyList<string> logins;
            try
            {
                logins = await new GitHubOrganizationLister()
                    .ListOrganizationsAsync(token, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // The lister translates auth failures and rate limiting into
                // InvalidOperationException.
                throw new CliErrorException(ex.Message, ex);
            }

            log.Info($"orgs finished: {logins.Count} login(s) listed.");

            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(logins, CliJsonContext.Default.IReadOnlyListString));
            }
            else
            {
                foreach (string login in logins)
                {
                    Console.Out.WriteLine(login);
                }
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log.Info("orgs canceled.");
            throw;
        }
        catch (Exception ex)
        {
            // Fatal path: missing or rejected token. Program prints the message;
            // the log keeps it.
            log.Error($"orgs failed: {ex.Message}", ex);
            throw;
        }
    }
}
