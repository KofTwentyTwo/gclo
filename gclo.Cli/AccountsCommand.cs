using System.Globalization;
using System.Text.Json;
using gclo.Engine;
using gclo.ViewModels;

namespace gclo.Cli;

/// <summary>'gclo accounts': list the saved accounts and their last sync outcome.</summary>
internal static class AccountsCommand
{
    private const string HelpText = """
        Usage: gclo accounts [options]

        Lists the saved accounts, one per line, in aligned columns:
        name, organization, target root, and last sync time (local time,
        'never' when the account has not completed a sync yet).

        An account pairs sync settings (organization, target root, parallelism,
        org-subfolder preference) with a token stored in Windows Credential
        Manager. Use one with 'gclo sync --account <name>'. Accounts are created
        and edited in the gclo desktop app for now; a CLI flag for creating them
        is planned. Because the token lives in Windows Credential Manager,
        accounts only work on Windows.

        Options:
          --json    Print a single-line JSON array instead:
                    [{"name":"...","organization":"...","targetRoot":"...",
                      "lastSync":"2026-07-04T15:30:00+00:00" or null}]
          --help    Show this help.

        Exit codes:
          0  listed successfully (also when no accounts exist yet)
          2  fatal: bad arguments, or not running on Windows
        """;

    public static int Run(string[] args)
    {
        bool json = false;

        var reader = new OptionReader(args);
        while (reader.MoveNext())
        {
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
                    throw new CliUsageException($"Unknown option '{reader.Current}' for 'gclo accounts'.");
            }
        }

        // One activity log per invocation; tokens are never read here, let alone logged.
        var log = new FileActivityLog();
        log.Info($"accounts started: json={json}");

        try
        {
            (AccountsStore store, _) = Open(log);
            IReadOnlyList<Account> accounts = store.GetAll();
            log.Info($"accounts finished: {accounts.Count} account(s) listed.");

            if (json)
            {
                IReadOnlyList<AccountSummary> summaries = accounts
                    .Select(a => new AccountSummary(a.Name, a.Organization, a.TargetRoot, a.LastSyncUtc))
                    .ToList();
                Console.Out.WriteLine(JsonSerializer.Serialize(
                    summaries, CliJsonContext.Default.IReadOnlyListAccountSummary));
                return 0;
            }

            if (accounts.Count == 0)
            {
                // Diagnostics go to stderr so redirected stdout stays clean (and empty).
                Console.Error.WriteLine("No accounts yet. Create one in the gclo desktop app.");
                return 0;
            }

            int nameWidth = accounts.Max(a => a.Name.Length);
            int orgWidth = accounts.Max(a => a.Organization.Length);
            int targetWidth = accounts.Max(a => a.TargetRoot.Length);
            foreach (Account account in accounts)
            {
                Console.Out.WriteLine(
                    $"{account.Name.PadRight(nameWidth)}  {account.Organization.PadRight(orgWidth)}  "
                    + $"{account.TargetRoot.PadRight(targetWidth)}  {FormatLastSync(account.LastSyncUtc)}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            // Fatal path: unusable accounts store or non-Windows platform.
            // Program prints the message; the log keeps it.
            log.Error($"accounts failed: {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>
    /// Opens the accounts store backed by Windows Credential Manager. The vault is
    /// returned alongside the store because token retrieval for 'gclo sync --account'
    /// goes through the vault directly (the store only handles metadata).
    /// Constructed lazily — only the account code paths call this — so plain
    /// 'gclo sync' and 'gclo orgs' never touch the credential store.
    /// </summary>
    /// <exception cref="CliErrorException">The current OS is not Windows.</exception>
    internal static (AccountsStore Store, ITokenVault Vault) Open(IActivityLog log)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new CliErrorException(
                "Accounts require Windows credential storage; "
                + "'gclo accounts' and 'gclo sync --account' only work on Windows.");
        }

        ITokenVault vault = new CredentialManagerVault();
        return (new AccountsStore(vault, log: log), vault);
    }

    /// <summary>'never', or the local time of the last completed sync at minute precision.</summary>
    private static string FormatLastSync(DateTimeOffset? lastSyncUtc)
        => lastSyncUtc is null
            ? "never"
            : lastSyncUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
