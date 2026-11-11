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
   /// <summary>Characters that no folder path can contain on Windows, wherever gclo runs.</summary>
   private static readonly System.Buffers.SearchValues<char> s_neverInAFolderPath = System.Buffers.SearchValues.Create("|<>\"*?");



   private const string HelpText = """
        Usage: gclo accounts [list] [--json]
               gclo accounts add --name <name> --org <name> --target <folder> [options] <token option>
               gclo accounts edit --name <name> [options] [token option]
               gclo accounts remove --name <name>

        An account pairs sync settings (organization, where it lives, target root,
        parallelism, org-subfolder preference, description) with a token stored in
        Windows Credential Manager. Use one with 'gclo sync --account <name>'.
        Because the token lives in Windows Credential Manager, accounts only work
        on Windows.

        list (default)
          Prints the accounts, one per line, in aligned columns: name,
          organization, where it lives ('GitHub', or the host of a Gitea server),
          target root, token source ('own' or 'default'), and last sync time
          (local time, 'never' when the account has not completed a sync yet).
          --json    Print a single-line JSON array instead:
                    [{"id":"...","name":"...","description":"...","organization":"...",
                      "targetRoot":"...","createOrgSubfolder":bool,"maxConcurrency":N,
                      "lastSync":"2026-07-04T15:30:00+00:00"|null,"lastSyncSummary":"..."|null,
                      "tokenSource":"own"|"default","forge":"github"|"gitea",
                      "server":"https://git.example.com/"|null}]

        add
          --name <name>          Display name; must be unique (case-insensitive).
          --org <name>           Organization or user account to sync.
          --forge <kind>         Where the organization lives: github (the default)
                                 or gitea (a Gitea or Forgejo server; needs --server).
          --server <address>     The address of the Gitea server, for example
                                 https://git.example.com. HTTPS only: it is where
                                 the token is sent. Implies --forge gitea. Both are
                                 fixed once the account is added.
          --target <folder>      Target root folder. A relative folder is stored as
                                 a full path, resolved against the current directory.
          --parallel <N>         Parallel clone/pull count, 1-64 (default 8).
          --org-subfolder        Clone under <target>\<org> instead of <target>.
          --description <text>   Optional note.
          --token-env <VAR> | --token-file <path> | --token-stdin
                                 The token to store (default: the GITHUB_TOKEN
                                 environment variable, or GITEA_TOKEN for a Gitea
                                 server). Never pass it as an argument.
          --use-default-token    Instead of a token of its own, the account uses
                                 the default token saved in the desktop app's
                                 Settings, read whenever it syncs, so replacing
                                 the default token there updates this account too.
                                 It is the default token of the place the account
                                 is on (GitHub, or its Gitea server): one has to
                                 be saved for that place.

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
          --use-default-token    Switch the account to the default token of the
                                 place it is on; its own token is removed from
                                 Windows Credential Manager.
          The forge and the server of an account cannot be changed: its token was
          saved for that place. Remove the account and add it again.

        remove
          --name <name>          Deletes the account's settings and its stored
                                 token. Repositories on disk are not touched.

        Exit codes:
          0  done
          2  fatal: bad arguments, unknown account, duplicate name, missing token,
             not running on Windows, or the accounts file or Windows Credential
             Manager could not be used (the message says why)
          4  another gclo process was changing the accounts; run the command again
        """;



   /// <summary>Composition root: wires the real Credential Manager store and file log.</summary>
   [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(
       Justification = "Wires the real Credential Manager and file log; delegates to the covered core.")]
   public static int Run(string[] args) => Run(args, Open, new FileActivityLog(gclo.ViewModels.GcloPaths.LogDirectory));



   internal static int Run(string[] args, Func<IActivityLog, (AccountsStore Store, ITokenVault Vault)> open, IActivityLog log)
   {
      // The first argument names the subcommand unless it is an option, in which
      // case it is 'list' (the original, option-only form of the command).
      // ('-h' is an option too: 'gclo accounts -h' asks for help, it names no subcommand.)
      string subcommand = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "list";
      string[] rest = string.Equals(subcommand, "list", StringComparison.Ordinal) && (args.Length == 0 || args[0].StartsWith('-'))
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
               Terminal.OutVerbatim(HelpText);
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
                    a.CreateOrgSubfolder, a.MaxConcurrency, a.LastSyncUtc, a.LastSyncSummary, TokenSourceLabel(a),
                    ForgeLabel(a), ServerOf(a)))
                .ToList();
            Terminal.OutVerbatim(JsonSerializer.Serialize(
                summaries, CliJsonContext.Default.IReadOnlyListAccountSummary));
            return ExitCodes.Success;
         }

         if(accounts.Count == 0)
         {
            // Diagnostics go to stderr so redirected stdout stays clean (and empty).
            Terminal.Error("No accounts yet. Create one with 'gclo accounts add' or in the gclo desktop app.");
            return ExitCodes.Success;
         }

         int nameWidth = accounts.Max(a => a.Name.Length);
         int orgWidth = accounts.Max(a => a.Organization.Length);
         int whereWidth = accounts.Max(a => Where(a).Length);
         int targetWidth = accounts.Max(a => a.TargetRoot.Length);
         foreach(Account account in accounts)
         {
            Terminal.Out(
                $"{account.Name.PadRight(nameWidth)}  {account.Organization.PadRight(orgWidth)}  {Where(account).PadRight(whereWidth)}  "
                + $"{account.TargetRoot.PadRight(targetWidth)}  {TokenSourceLabel(account).PadRight(7)}  {FormatLastSync(account.LastSyncUtc)}");
         }
         return ExitCodes.Success;
      }
      catch(Exception ex) when(CliErrorException.IsStorageFailure(ex))
      {
         log.Error($"accounts failed: {ex.Message}", ex);
         throw CliErrorException.FromStorage(ex);
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
               description = reader.RequireValueOrEmpty();
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

      string targetRoot = AbsoluteTarget(target);
      ForgeEndpoint endpoint = forgeOptions.Resolve();

      log.Info($"accounts add started: name='{name.Trim()}', org='{org.Trim()}', forge={endpoint}, token={(tokenOptions.UseDefault ? "default" : "own")}");
      try
      {
         // The token (if the account gets one of its own) is read before the store
         // opens, so a bad token option fails without touching accounts.json.
         string? token = tokenOptions.UseDefault ? null : tokenOptions.Resolve(endpoint.Kind);
         (AccountsStore store, ITokenVault vault) = open(log);
         RequireDefaultTokenIfUsed(tokenOptions, vault, endpoint);
         var account = new Account
         {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Description = description.Trim(),
            Organization = org.Trim(),
            TargetRoot = targetRoot,
            CreateOrgSubfolder = orgSubfolder,
            MaxConcurrency = parallel,
            TokenSource = tokenOptions.UseDefault ? TokenSource.Default : TokenSource.Own,
            Forge = endpoint.Kind,
            Server = endpoint.Server?.AbsoluteUri,
         };
         SaveOrFail(store, account, token);
         Terminal.Out(
             $"Account '{account.Name}' added"
             + (endpoint.Kind == ForgeKind.GitHub ? "" : $" on {endpoint}")
             + (tokenOptions.UseDefault ? " (uses the default token)." : "."));
         return ExitCodes.Success;
      }
      catch(Exception ex) when(CliErrorException.IsStorageFailure(ex))
      {
         log.Error($"accounts add failed: {ex.Message}", ex);
         throw CliErrorException.FromStorage(ex);
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
               Terminal.OutVerbatim(HelpText);
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
               description = reader.RequireValueOrEmpty();
               break;
            case "--parallel":
               parallel = OptionReader.ParseParallel(reader.RequireValue());
               break;
            case "--org-subfolder":
               reader.RejectValue();
               orgSubfolder = true;
               break;
            case "--forge" or "--server":
               throw new CliUsageException(
                   "The forge and the server of an account cannot be changed: its token was saved for that place. "
                   + "Remove the account and add it again.");
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

      RequireNotBlank(rename, "--rename");
      RequireNotBlank(org, "--org");
      RequireNotBlank(target, "--target");
      string? targetRoot = target is null ? null : AbsoluteTarget(target);

      log.Info($"accounts edit started: name='{name.Trim()}'");
      try
      {
         (AccountsStore store, ITokenVault vault) = open(log);
         Account existing = store.FindByName(name.Trim()) ?? throw UnknownAccount(name.Trim(), store);
         RefuseASharedName(store, existing.Name);
         tokenOptions.EnsureAtMostOneSource();
         if(tokenOptions.UseDefault)
         {
            // The default token of the place the account is on; an account whose place cannot be used has none.
            ForgeEndpoint place = existing.TryGetEndpoint(out ForgeEndpoint? where, out string? unusable)
                ? where
                : throw new CliErrorException(
                    $"Account '{existing.Name}' cannot be used as it is saved: {unusable}. {existing.AdviceWhenUnusable("Remove")}");
            RequireDefaultTokenIfUsed(tokenOptions, vault, place);
         }
         string? token = tokenOptions.HasExplicitSource && !tokenOptions.UseDefault ? tokenOptions.Resolve(existing.Forge) : null;

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
            TargetRoot = targetRoot ?? existing.TargetRoot,
            Description = description?.Trim() ?? existing.Description,
            MaxConcurrency = parallel ?? existing.MaxConcurrency,
            CreateOrgSubfolder = orgSubfolder ?? existing.CreateOrgSubfolder,
            TokenSource = source,
         };
         SaveOrFail(store, updated, token);
         string tokenNote = tokenOptions.UseDefault
             ? " (now uses the default token)."
             : token is null ? "." : " (token replaced).";
         Terminal.Out($"Account '{updated.Name}' updated" + tokenNote);
         return ExitCodes.Success;
      }
      catch(Exception ex) when(CliErrorException.IsStorageFailure(ex))
      {
         log.Error($"accounts edit failed: {ex.Message}", ex);
         throw CliErrorException.FromStorage(ex);
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
               Terminal.OutVerbatim(HelpText);
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
         RefuseASharedName(store, existing.Name);
         store.Delete(existing.Id); // the store logs the deletion
         Terminal.Out($"Account '{existing.Name}' removed.");
         return ExitCodes.Success;
      }
      catch(Exception ex) when(CliErrorException.IsStorageFailure(ex))
      {
         log.Error($"accounts remove failed: {ex.Message}", ex);
         throw CliErrorException.FromStorage(ex);
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



   /// <summary>The forge as the JSON listing names it: 'github', 'gitea', or 'unknown' for one this version does not know.</summary>
   private static string ForgeLabel(Account account)
       => !account.IsOnAKnownForge ? "unknown"
           : account.Forge == ForgeKind.GitHub ? "github"
           : "gitea";



   /// <summary>
   /// The server of an account as the JSON listing gives it: the address the token
   /// is sent to, as it is normalized, and nothing for an account whose stored
   /// address cannot be used. What the file says is not repeated as it is: an
   /// address that cannot be used may be one with a password or a token in it.
   /// </summary>
   private static string? ServerOf(Account account)
       => account.TryGetEndpoint(out ForgeEndpoint? endpoint, out _) ? endpoint.Server?.AbsoluteUri : null;



   /// <summary>Where an account lives, as the listing and messages name it: "GitHub", or the host of its server.</summary>
   private static string Where(Account account)
       => account.TryGetEndpoint(out ForgeEndpoint? endpoint, out _) ? endpoint.DisplayName : $"{ForgeLabel(account)} (not usable)";



   /// <summary>
   /// --use-default-token is only useful when a default token exists for the place
   /// the account is on: without one the account could never sync, so the command
   /// fails up front and says where the default token is set (the CLI has no way to
   /// set it). A default token saved for another place does not count.
   /// </summary>
   private static void RequireDefaultTokenIfUsed(TokenOptions tokenOptions, ITokenVault vault, ForgeEndpoint place)
   {
      tokenOptions.EnsureAtMostOneSource();
      if(tokenOptions.UseDefault && !new AccountTokenResolver(vault).HasDefaultTokenFor(place))
      {
         throw new CliErrorException(
             $"No default token is saved for {place}, so the account could not sync. Save one in the desktop app "
             + "(File > Settings), or give the account a token of its own with --token-env, "
             + "--token-file, or --token-stdin.");
      }
   }



   /// <summary>A value that was given must say something: a blank name, organization, or folder is a mistake.</summary>
   private static void RequireNotBlank(string? value, string option)
   {
      if(value is not null && string.IsNullOrWhiteSpace(value))
      {
         throw new CliUsageException($"{option} expects a value that is not blank.");
      }
   }



   /// <summary>
   /// The target folder as a full path. An account is used later from other places
   /// (the desktop app, a scheduled task, another working directory); a relative
   /// folder would mean a different one each time, so it is resolved once, here.
   /// What cannot be a folder is refused instead of being stored: characters no
   /// folder name can have, a device name, and text that a shell was meant to expand
   /// and did not ('~', '%NAME%'), which would otherwise become a folder of that name.
   /// </summary>
   private static string AbsoluteTarget(string target)
   {
      string trimmed = target.Trim();
      string? problem = trimmed.AsSpan().ContainsAny(s_neverInAFolderPath) || trimmed.Any(char.IsControl)
          ? "it has a character no folder name can have"
          : trimmed.StartsWith('~') || trimmed.Contains('%')
              ? "'~' and '%NAME%' are not expanded by gclo; give the folder itself"
              : !trimmed.StartsWith(@"\\", StringComparison.Ordinal) && trimmed.Where((c, i) => c == ':' && i != 1).Any()
                  ? "a colon belongs after a drive letter only"
                  : null;

      string full = "";
      if(problem is null)
      {
         try
         {
            full = Path.GetFullPath(trimmed);
         }
         catch(Exception ex) when(ex is ArgumentException or PathTooLongException)
         {
            problem = ex.Message;
         }
      }
      if(problem is null && full.StartsWith(@"\\.\", StringComparison.Ordinal))
      {
         problem = "it names a device, not a folder";
      }

      return problem is null
          ? full
          : throw new CliUsageException($"--target '{target}' is not a usable folder path: {problem}.");
   }



   /// <summary>Saves, turning what the store refuses (a duplicate name, an account removed meanwhile) into a fatal error with its reason.</summary>
   private static void SaveOrFail(AccountsStore store, Account account, string? token)
   {
      try
      {
         store.Save(account, token);
      }
      catch(Exception ex) when(ex is ArgumentException or InvalidOperationException)
      {
         // A name that is taken, or an account that another gclo process removed
         // while this command was waiting: the store says which, and nothing was saved.
         throw new CliErrorException(ex.Message, ex);
      }
   }



   /// <summary>
   /// Two accounts of one name: the command does not choose one of them. It can
   /// only come about through a version of gclo from before forges, which does not
   /// see the accounts on another forge and so does not know the name is taken.
   /// </summary>
   /// <exception cref="CliErrorException">More than one account is named <paramref name="name"/>.</exception>
   internal static void RefuseASharedName(AccountsStore store, string name)
   {
      if(store.IsNameShared(name))
      {
         throw new CliErrorException(
             $"Two accounts are named '{name}', so the name does not say which one is meant. "
             + "Give one of them another name in the gclo desktop app (Edit account), then try again.");
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
