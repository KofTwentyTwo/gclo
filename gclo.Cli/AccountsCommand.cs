/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Text.Json;
using gclo.Engine;
using gclo.ViewModels;


namespace gclo.Cli;


/// <summary>
/// 'gclo accounts': list, add, edit, and remove the saved accounts — the same
/// profiles the desktop app's account wizard manages, so a headless mirror server
/// can set up 'gclo sync --account' without the GUI.
/// </summary>
internal static class AccountsCommand
{
   private const string HelpText = """
        Usage: gclo accounts [list] [--json]
               gclo accounts add --name <name> --org <name> --target <folder> [options] <token option>
               gclo accounts edit --name <name> [options] [token option]
               gclo accounts remove --name <name>

        An account pairs sync settings (organization, target root, parallelism,
        org-subfolder preference, description) with a token stored in Windows
        Credential Manager. Use one with 'gclo sync --account <name>'. Because the
        token lives in Windows Credential Manager, accounts only work on Windows.

        list (default)
          Prints the accounts, one per line, in aligned columns: name,
          organization, target root, token source ('own' or 'default'), and last
          sync time (local time, 'never' when the account has not completed a
          sync yet).
          --json    Print a single-line JSON array instead:
                    [{"id":"...","name":"...","description":"...","organization":"...",
                      "targetRoot":"...","createOrgSubfolder":bool,"maxConcurrency":N,
                      "lastSync":"2026-07-04T15:30:00+00:00"|null,"lastSyncSummary":"..."|null,
                      "tokenSource":"own"|"default"}]

        add
          --name <name>          Display name; must be unique (case-insensitive).
          --org <name>           GitHub organization or user account to sync.
          --target <folder>      Target root folder.
          --parallel <N>         Parallel clone/pull count, 1-64 (default 8).
          --org-subfolder        Clone under <target>\<org> instead of <target>.
          --description <text>   Optional note.
          --token-env <VAR> | --token-file <path> | --token-stdin
                                 The token to store (default: the GITHUB_TOKEN
                                 environment variable). Never pass it as an argument.
          --use-default-token    Instead of a token of its own, the account uses
                                 the default token saved in the desktop app's
                                 Settings, read whenever it syncs, so replacing
                                 the default token there updates this account too.

        edit
          --name <name>          The account to change (required).
          --rename <new name>    New display name.
          --org, --target, --parallel, --description
                                 Replace that setting.
          --org-subfolder | --no-org-subfolder
                                 Turn the organization subfolder on or off.
          --token-env | --token-file | --token-stdin
                                 Replace the stored token (and switch an account
                                 on the default token to a token of its own);
                                 without a token option the token is left untouched.
          --use-default-token    Switch the account to the default token; its own
                                 token is removed from Windows Credential Manager.

        remove
          --name <name>          Deletes the account's settings and its stored
                                 token. Repositories on disk are not touched.

        Exit codes:
          0  done
          2  fatal: bad arguments, unknown account, duplicate name, missing token,
             or not running on Windows
        """;



   /// <summary>Composition root: wires the real Credential Manager store and file log.</summary>
   [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(
       Justification = "Wires the real Credential Manager and file log; delegates to the covered core.")]
   public static int Run(string[] args) => Run(args, Open, new FileActivityLog());



   internal static int Run(string[] args, Func<IActivityLog, (AccountsStore Store, ITokenVault Vault)> open, IActivityLog log)
   {
      // The first argument names the subcommand unless it is an option, in which
      // case it is 'list' (the original, option-only form of the command).
      string subcommand = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "list";
      string[] rest = string.Equals(subcommand, "list", StringComparison.Ordinal) && (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
          ? args
          : args[1..];

      return subcommand switch
      {
         "list" => List(rest, open, log),
         "add" => Add(rest, open, log),
         "edit" => Edit(rest, open, log),
         "remove" => Remove(rest, open, log),
         _ => throw new CliUsageException($"Unknown subcommand '{subcommand}' for 'gclo accounts' (use list, add, edit, or remove)."),
      };
   }



   // ---------------------------------------------------------------- list

   private static int List(string[] args, Func<IActivityLog, (AccountsStore Store, ITokenVault Vault)> open, IActivityLog log)
   {
      bool json = false;

      var reader = new OptionReader(args);
      while(reader.MoveNext())
      {
         switch(reader.Current)
         {
            case "--help" or "-h":
               Console.Out.WriteLine(HelpText);
               return ExitCodes.Success;
            case "--json":
               reader.RejectValue();
               json = true;
               break;
            default:
               throw new CliUsageException($"Unknown option '{reader.Current}' for 'gclo accounts'.");
         }
      }

      log.Info($"accounts started: json={json}");

      try
      {
         (AccountsStore store, _) = open(log);
         IReadOnlyList<Account> accounts = store.GetAll();
         log.Info($"accounts finished: {accounts.Count} account(s) listed.");

         if(json)
         {
            IReadOnlyList<AccountSummary> summaries = accounts
                .Select(a => new AccountSummary(
                    a.Id.ToString("N"), a.Name, a.Description, a.Organization, a.TargetRoot,
                    a.CreateOrgSubfolder, a.MaxConcurrency, a.LastSyncUtc, a.LastSyncSummary, TokenSourceLabel(a)))
                .ToList();
            Console.Out.WriteLine(JsonSerializer.Serialize(
                summaries, CliJsonContext.Default.IReadOnlyListAccountSummary));
            return ExitCodes.Success;
         }

         if(accounts.Count == 0)
         {
            // Diagnostics go to stderr so redirected stdout stays clean (and empty).
            Console.Error.WriteLine("No accounts yet. Create one with 'gclo accounts add' or in the gclo desktop app.");
            return ExitCodes.Success;
         }

         int nameWidth = accounts.Max(a => a.Name.Length);
         int orgWidth = accounts.Max(a => a.Organization.Length);
         int targetWidth = accounts.Max(a => a.TargetRoot.Length);
         foreach(Account account in accounts)
         {
            Console.Out.WriteLine(
                $"{account.Name.PadRight(nameWidth)}  {account.Organization.PadRight(orgWidth)}  "
                + $"{account.TargetRoot.PadRight(targetWidth)}  {TokenSourceLabel(account).PadRight(7)}  {FormatLastSync(account.LastSyncUtc)}");
         }
         return ExitCodes.Success;
      }
      catch(Exception ex)
      {
         // Fatal path: unusable accounts store or non-Windows platform.
         // Program prints the message; the log keeps it.
         log.Error($"accounts failed: {ex.Message}", ex);
         throw;
      }
   }



   // ---------------------------------------------------------------- add

   private static int Add(string[] args, Func<IActivityLog, (AccountsStore Store, ITokenVault Vault)> open, IActivityLog log)
   {
      string? name = null;
      string? org = null;
      string? target = null;
      string description = "";
      int parallel = AppSettings.DefaultConcurrency;
      bool orgSubfolder = false;
      var tokenOptions = new TokenOptions();

      var reader = new OptionReader(args);
      while(reader.MoveNext())
      {
         if(tokenOptions.TryConsume(reader))
         {
            continue;
         }
         switch(reader.Current)
         {
            case "--help" or "-h":
               Console.Out.WriteLine(HelpText);
               return ExitCodes.Success;
            case "--name":
               name = reader.RequireValue();
               break;
            case "--org":
               org = reader.RequireValue();
               break;
            case "--target":
               target = reader.RequireValue();
               break;
            case "--description":
               description = reader.RequireValue();
               break;
            case "--parallel":
               parallel = OptionReader.ParseParallel(reader.RequireValue());
               break;
            case "--org-subfolder":
               reader.RejectValue();
               orgSubfolder = true;
               break;
            default:
               throw new CliUsageException($"Unknown option '{reader.Current}' for 'gclo accounts add'.");
         }
      }

      if(string.IsNullOrWhiteSpace(name))
      {
         throw new CliUsageException("--name is required.");
      }
      if(string.IsNullOrWhiteSpace(org))
      {
         throw new CliUsageException("--org is required.");
      }
      if(string.IsNullOrWhiteSpace(target))
      {
         throw new CliUsageException("--target is required.");
      }

      log.Info($"accounts add started: name='{name.Trim()}', org='{org.Trim()}', token={(tokenOptions.UseDefault ? "default" : "own")}");
      try
      {
         // The token (if the account gets one of its own) is read before the store
         // opens, so a bad token option fails without touching accounts.json.
         string? token = tokenOptions.UseDefault ? null : tokenOptions.Resolve();
         (AccountsStore store, ITokenVault vault) = open(log);
         RequireDefaultTokenIfUsed(tokenOptions, vault);
         var account = new Account
         {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Description = description.Trim(),
            Organization = org.Trim(),
            TargetRoot = target.Trim(),
            CreateOrgSubfolder = orgSubfolder,
            MaxConcurrency = parallel,
            TokenSource = tokenOptions.UseDefault ? TokenSource.Default : TokenSource.Own,
         };
         SaveOrFail(store, account, token);
         Console.Out.WriteLine($"Account '{account.Name}' added" + (tokenOptions.UseDefault ? " (uses the default token)." : "."));
         return ExitCodes.Success;
      }
      catch(Exception ex)
      {
         log.Error($"accounts add failed: {ex.Message}", ex);
         throw;
      }
   }



   // ---------------------------------------------------------------- edit

   private static int Edit(string[] args, Func<IActivityLog, (AccountsStore Store, ITokenVault Vault)> open, IActivityLog log)
   {
      string? name = null;
      string? rename = null;
      string? org = null;
      string? target = null;
      string? description = null;
      int? parallel = null;
      bool? orgSubfolder = null;
      var tokenOptions = new TokenOptions();

      var reader = new OptionReader(args);
      while(reader.MoveNext())
      {
         if(tokenOptions.TryConsume(reader))
         {
            continue;
         }
         switch(reader.Current)
         {
            case "--help" or "-h":
               Console.Out.WriteLine(HelpText);
               return ExitCodes.Success;
            case "--name":
               name = reader.RequireValue();
               break;
            case "--rename":
               rename = reader.RequireValue();
               break;
            case "--org":
               org = reader.RequireValue();
               break;
            case "--target":
               target = reader.RequireValue();
               break;
            case "--description":
               description = reader.RequireValue();
               break;
            case "--parallel":
               parallel = OptionReader.ParseParallel(reader.RequireValue());
               break;
            case "--org-subfolder":
               reader.RejectValue();
               orgSubfolder = true;
               break;
            case "--no-org-subfolder":
               reader.RejectValue();
               orgSubfolder = false;
               break;
            default:
               throw new CliUsageException($"Unknown option '{reader.Current}' for 'gclo accounts edit'.");
         }
      }

      if(string.IsNullOrWhiteSpace(name))
      {
         throw new CliUsageException("--name is required.");
      }
      if(rename is null && org is null && target is null && description is null && parallel is null
          && orgSubfolder is null && !tokenOptions.HasExplicitSource)
      {
         throw new CliUsageException("Nothing to change: give at least one of --rename, --org, --target, --parallel, "
             + "--description, --org-subfolder/--no-org-subfolder, or a token option.");
      }

      log.Info($"accounts edit started: name='{name.Trim()}'");
      try
      {
         (AccountsStore store, ITokenVault vault) = open(log);
         Account existing = store.FindByName(name.Trim()) ?? throw UnknownAccount(name.Trim(), store);
         RequireDefaultTokenIfUsed(tokenOptions, vault);
         string? token = tokenOptions.HasExplicitSource && !tokenOptions.UseDefault ? tokenOptions.Resolve() : null;

         // A token option decides the source: --use-default-token switches to the
         // default token (and drops the own entry); --token-* gives the account a
         // token of its own; neither leaves the source and the token untouched.
         TokenSource source = tokenOptions.UseDefault
             ? TokenSource.Default
             : token is null ? existing.TokenSource : TokenSource.Own;
         Account updated = existing with
         {
            Name = rename?.Trim() ?? existing.Name,
            Organization = org?.Trim() ?? existing.Organization,
            TargetRoot = target?.Trim() ?? existing.TargetRoot,
            Description = description?.Trim() ?? existing.Description,
            MaxConcurrency = parallel ?? existing.MaxConcurrency,
            CreateOrgSubfolder = orgSubfolder ?? existing.CreateOrgSubfolder,
            TokenSource = source,
         };
         SaveOrFail(store, updated, token);
         string tokenNote = tokenOptions.UseDefault
             ? " (now uses the default token)."
             : token is null ? "." : " (token replaced).";
         Console.Out.WriteLine($"Account '{updated.Name}' updated" + tokenNote);
         return ExitCodes.Success;
      }
      catch(Exception ex)
      {
         log.Error($"accounts edit failed: {ex.Message}", ex);
         throw;
      }
   }



   // ---------------------------------------------------------------- remove

   private static int Remove(string[] args, Func<IActivityLog, (AccountsStore Store, ITokenVault Vault)> open, IActivityLog log)
   {
      string? name = null;

      var reader = new OptionReader(args);
      while(reader.MoveNext())
      {
         switch(reader.Current)
         {
            case "--help" or "-h":
               Console.Out.WriteLine(HelpText);
               return ExitCodes.Success;
            case "--name":
               name = reader.RequireValue();
               break;
            default:
               throw new CliUsageException($"Unknown option '{reader.Current}' for 'gclo accounts remove'.");
         }
      }

      if(string.IsNullOrWhiteSpace(name))
      {
         throw new CliUsageException("--name is required.");
      }

      log.Info($"accounts remove started: name='{name.Trim()}'");
      try
      {
         (AccountsStore store, _) = open(log);
         Account existing = store.FindByName(name.Trim()) ?? throw UnknownAccount(name.Trim(), store);
         store.Delete(existing.Id); // the store logs the deletion
         Console.Out.WriteLine($"Account '{existing.Name}' removed.");
         return ExitCodes.Success;
      }
      catch(Exception ex)
      {
         log.Error($"accounts remove failed: {ex.Message}", ex);
         throw;
      }
   }



   // ---------------------------------------------------------------- shared

   /// <summary>'own' or 'default': the token source as the list column and the JSON field show it.</summary>
   private static string TokenSourceLabel(Account account) => account.UsesDefaultToken ? "default" : "own";



   /// <summary>
   /// --use-default-token is only useful when a default token exists: without one
   /// the account could never sync, so the command fails up front and says where
   /// the default token is set (the CLI has no way to set it).
   /// </summary>
   private static void RequireDefaultTokenIfUsed(TokenOptions tokenOptions, ITokenVault vault)
   {
      tokenOptions.EnsureAtMostOneSource();
      if(tokenOptions.UseDefault && !new AccountTokenResolver(vault).HasDefaultToken)
      {
         throw new CliErrorException(
             "No default token is saved, so the account could not sync. Save one in the desktop app "
             + "(Settings > Default GitHub token), or give the account a token of its own with --token-env, "
             + "--token-file, or --token-stdin.");
      }
   }



   /// <summary>A duplicate name is the one validation the store itself enforces; everything else is fatal as is.</summary>
   private static void SaveOrFail(AccountsStore store, Account account, string? token)
   {
      try
      {
         store.Save(account, token);
      }
      catch(ArgumentException ex)
      {
         throw new CliErrorException(ex.Message, ex);
      }
   }



   private static CliErrorException UnknownAccount(string name, AccountsStore store)
   {
      IReadOnlyList<Account> all = store.GetAll();
      string available = all.Count == 0
          ? "No accounts exist yet."
          : "Available accounts: " + string.Join(", ", all.Select(a => a.Name)) + ".";
      return new CliErrorException($"No account named '{name}'. {available}");
   }



   /// <summary>
   /// Opens the accounts store backed by Windows Credential Manager. The vault is
   /// returned alongside the store because token retrieval for 'gclo sync --account'
   /// goes through the vault directly (the store only handles metadata).
   /// Constructed lazily — only the account code paths call this — so plain
   /// 'gclo sync' and 'gclo orgs' never touch the credential store.
   /// </summary>
   /// <exception cref="CliErrorException">The current OS is not Windows.</exception>
   [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(
       Justification = "Wires the real Credential Manager vault; its non-Windows guard mirrors the vault's own tested guard.")]
   internal static (AccountsStore Store, ITokenVault Vault) Open(IActivityLog log)
   {
      if(!OperatingSystem.IsWindows())
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
