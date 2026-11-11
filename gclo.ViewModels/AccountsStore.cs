/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.Engine;


namespace gclo.ViewModels;


/// <summary>
/// Persists account profiles as JSON under <see cref="GcloPaths.DataRoot"/> (default
/// %LOCALAPPDATA%\KofTwentyTwo\gclo) and their tokens in an <see cref="ITokenVault"/>;
/// the files hold metadata only and never a token. GitHub accounts are in
/// accounts.json, as they always were; accounts on another forge are in
/// accounts-forges.json (see the remarks). Unlike <see cref="AppSettings"/>, accounts are primary user data, so write
/// failures in <see cref="Save"/>, <see cref="Delete"/>, and
/// <see cref="RecordSyncResult"/> propagate instead of being swallowed; only loading
/// is tolerant (a missing, corrupt, or unreadable file yields an empty list).
/// </summary>
/// <remarks>
/// <para>
/// Metadata and the vault are two stores with no shared transaction, so every
/// operation that touches both has a fixed order and a compensating step:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Save with a token:</b> vault write first, then metadata. A vault failure
/// changes nothing (the vault's <see cref="ITokenVault.Store"/> either replaces the
/// entry or leaves the previous one intact, so an update keeps its working
/// credential). A metadata failure is compensated by restoring the previous token
/// (or deleting the entry for a brand-new account).
/// </description></item>
/// <item><description>
/// <b>Delete:</b> vault delete first, then metadata. A vault failure changes nothing.
/// A metadata failure is compensated by re-storing the token that was removed.
/// </description></item>
/// <item><description>
/// <b>Compensation failure</b> surfaces as <see cref="AccountConsistencyException"/>
/// whose message states the exact leftover state. Every such state is reconciled by
/// simply retrying the same operation: Save overwrites the vault entry and rewrites
/// the file; Delete tolerates an absent vault entry.
/// </description></item>
/// </list>
/// <para>A change shows in <see cref="GetAll"/> only after both stores succeeded. Nothing here logs a token.</para>
/// <para>
/// The desktop app and the CLI share the file, and each may run more than once. Every
/// change therefore takes a lock that every gclo process takes (accounts.json.lock,
/// opened for this process alone), reads the file again, applies its one change by
/// <see cref="Account.Id"/> to what the file holds now, and writes that: an account
/// another process added or removed in the meantime stays added or removed, and a
/// sync result it recorded is kept. When two processes edit the same account, the
/// later edit wins; an edit of an account that another process removed is refused
/// instead of bringing it back. Reads (<see cref="GetAll"/>, <see cref="FindByName"/>)
/// answer from what this store last read, at construction or at its latest change;
/// that is also what the store holds after a change that failed.
/// </para>
/// <para>
/// <b>Two files.</b> A version of gclo from before forges reads every account in
/// accounts.json as a GitHub account, and writes the file back with what it knows:
/// it would drop the forge and the server of an account on a Gitea server, and from
/// then on that account would read as a GitHub account, whose token is sent to
/// GitHub. The desktop app updates itself and the command line is a separate
/// download, so two versions on one data folder are ordinary. Accounts on another
/// forge are therefore kept in a file of their own, which such a version neither
/// reads nor writes. Both files are changed under the one lock. An account on
/// another forge that is found in accounts.json (gclo does not put one there) is
/// moved to its own file before the next change is applied, and one that is in
/// both files is read from its own. A record in the file for other forges that
/// names no forge is not a GitHub account: it stays there, and cannot be used.
/// </para>
/// <para>
/// A change itself writes one file, the one of the account it changes. Whatever
/// else has to be written (a file that went missing or was damaged, an account
/// that is in the wrong file) is written first, before the vault is touched: when
/// that fails the change fails with nothing changed, and the order and the
/// compensating steps above hold for one file as they always did.
/// </para>
/// <para>
/// A file that cannot be read is not a corrupt file. Only content that is not valid
/// JSON is moved aside. A file that is in use is tried again for a short while; one
/// that stays unreadable is left exactly where it is: at construction the store then
/// starts empty (and logs why), and a change fails without writing anything. A
/// change that finds the file gone or damaged does not start over from nothing: it
/// is applied to what this store last read, which puts the accounts back.
/// </para>
/// </remarks>
public sealed class AccountsStore
{
   private readonly ITokenVault _vault;

   private readonly IActivityLog _log;

   /// <summary>accounts.json: the GitHub accounts. Read and written by every version of gclo.</summary>
   private readonly AccountsFile _gitHubFile;

   /// <summary>accounts-forges.json: the accounts on another forge. A version from before forges does not touch it.</summary>
   private readonly AccountsFile _forgesFile;

   /// <summary>The lock every gclo process takes before it changes an accounts file.</summary>
   private readonly string _lockPath;

   /// <summary>How long the lock another gclo process holds is waited for.</summary>
   private static readonly TimeSpan s_defaultPatience = TimeSpan.FromSeconds(5);

   /// <summary>
   /// The share of the patience spent on a file that something outside gclo holds for
   /// a moment (reading it, or swapping the new one into place). Kept well below the
   /// wait for the lock, so a process that holds the lock while it waits here does
   /// not outlast the patience of the ones waiting for it.
   /// </summary>
   private const int FilePatienceDivisor = 5;

   private readonly Lock _gate = new();

   private readonly TimeSpan _patience;

   private List<Account> _accounts;



   /// <summary>
   /// Loads existing accounts from <paramref name="directory"/> (default:
   /// <see cref="GcloPaths.DataRoot"/>). A missing file simply means no accounts yet;
   /// a corrupt or unreadable one is logged (pass the app's <paramref name="log"/> to
   /// surface that) and treated as empty rather than blocking startup.
   /// </summary>
   public AccountsStore(ITokenVault vault, string? directory = null, IActivityLog? log = null)
       : this(vault, directory, log, s_defaultPatience)
   {
   }



   /// <summary>Creates a store with its own patience for a busy file, for tests.</summary>
   internal AccountsStore(ITokenVault vault, string? directory, IActivityLog? log, TimeSpan patience)
   {
      ArgumentNullException.ThrowIfNull(vault);
      _vault = vault;
      _log = log ?? new NullActivityLog();
      directory ??= GcloPaths.DataRoot;
      _patience = patience;
      _gitHubFile = new AccountsFile(Path.Combine(directory, "accounts.json"), _log, patience / FilePatienceDivisor);
      _forgesFile = new AccountsFile(Path.Combine(directory, "accounts-forges.json"), _log, patience / FilePatienceDivisor);
      _lockPath = _gitHubFile.FilePath + ".lock";
      _accounts = LoadAtStart();
   }



   /// <summary>All accounts, sorted by <see cref="Account.Name"/> (case-insensitive).</summary>
   public IReadOnlyList<Account> GetAll()
   {
      lock(_gate)
      {
         return _accounts.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
      }
   }



   /// <summary>The account whose name matches case-insensitively, or null. See <see cref="IsNameShared"/>.</summary>
   public Account? FindByName(string name)
   {
      lock(_gate)
      {
         return _accounts.FirstOrDefault(
             a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
      }
   }



   /// <summary>
   /// True when more than one account has the name. <see cref="Save"/> does not let
   /// that happen, but a version of gclo from before forges does not see the accounts
   /// on another forge and can give a GitHub account the name of one of them. A
   /// caller that is given a name to act on asks here first, so it does not act on
   /// the wrong one of the two.
   /// </summary>
   public bool IsNameShared(string name)
   {
      lock(_gate)
      {
         return _accounts.Count(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) > 1;
      }
   }



   /// <summary>
   /// Inserts or updates (matching by <see cref="Account.Id"/>) and persists
   /// immediately, on top of what the file holds at that moment. Throws <see cref="ArgumentException"/> when another account
   /// already uses the name (case-insensitive); nothing is persisted in that case.
   /// A non-null <paramref name="token"/> is written to the vault BEFORE the
   /// metadata; if the metadata write then fails, the vault is put back the way it
   /// was (previous token restored, or the new entry removed) and the failure
   /// propagates. A null token leaves any existing vault entry untouched.
   /// See the class remarks for the full consistency contract.
   /// </summary>
   public void Save(Account account, string? token)
   {
      ArgumentNullException.ThrowIfNull(account);
      lock(_gate)
      {
         bool knownBefore = _accounts.Any(a => a.Id == account.Id);

         ///////////////////////////////////////////////////////////////////////////
         // Another gclo process may have changed the file since it was last read. //
         // The change is applied to what the file holds now, under the lock every //
         // gclo process takes, so nothing another process wrote is written over.  //
         ///////////////////////////////////////////////////////////////////////////
         using FileStream _ = AcquireFileLock();
         RefreshForAChange();

         account = ReconcileWithTheFile(account, knownBefore);
         account = WithItsForgeChecked(account);

         bool nameTaken = _accounts.Any(a =>
             a.Id != account.Id
             && string.Equals(a.Name, account.Name, StringComparison.OrdinalIgnoreCase));
         if(nameTaken)
         {
            throw new ArgumentException(
                $"An account named '{account.Name}' already exists.", nameof(account));
         }

         var updated = new List<Account>(_accounts);
         int index = updated.FindIndex(a => a.Id == account.Id);
         if(index >= 0)
         {
            updated[index] = account;
         }
         else
         {
            updated.Add(account);
         }

         string verb = index >= 0 ? "updated" : "added";
         if(account.UsesDefaultToken)
         {
            if(token is not null)
            {
               throw new ArgumentException(
                   "An account that uses the default token has no token of its own to store.", nameof(token));
            }
            SaveUsingDefaultToken(updated, account, verb);
            return;
         }

         if(token is null)
         {
            Persist(updated); // IO failures propagate; _accounts stays unchanged then.
            _accounts = updated;
            _log.Info($"Account '{account.Name}' {verb} (token unchanged).");
            return;
         }

         // Vault first: a failing Store leaves the vault exactly as it was (the
         // previous token, if any, is still there), and nothing else has changed.
         string? previousToken = _vault.TryRetrieve(account.TokenEntryId);
         _vault.Store(account.TokenEntryId, token);

         try
         {
            Persist(updated);
         }
         catch(Exception persistFailure)
         {
            // Undo the vault write so no entry points at metadata that was never saved.
            try
            {
               if(previousToken is null)
               {
                  _vault.Delete(account.TokenEntryId);
               }
               else
               {
                  _vault.Store(account.TokenEntryId, previousToken);
               }
            }
            catch(Exception compensationFailure)
            {
               string leftover = previousToken is null
                   ? $"the vault holds a token for account {account.Id:N} ('{account.Name}') that is not in accounts.json; "
                     + "saving the account again overwrites it, deleting the account removes it"
                   : $"the vault holds the NEW token for account {account.Id:N} ('{account.Name}') while accounts.json "
                     + "still has the previous metadata; saving the account again reconciles both";
               _log.Error($"Account save failed and the vault could not be restored: {leftover}.", compensationFailure);
               throw new AccountConsistencyException(
                   $"Saving account '{account.Name}' failed ({persistFailure.Message}), and restoring the token vault "
                   + $"failed too ({compensationFailure.Message}). Current state: {leftover}.",
                   persistFailure, compensationFailure);
            }

            throw;
         }

         _accounts = updated;
         _log.Info($"Account '{account.Name}' {verb} with a new token.");
      }
   }



   /// <summary>
   /// Saves an account that resolves to the default token: its metadata is written
   /// and any token of its own is removed from the vault, vault first, with the same
   /// put-it-back compensation as a delete. Called under the gate.
   /// </summary>
   private void SaveUsingDefaultToken(List<Account> updated, Account account, string verb)
   {
      string? ownToken = _vault.TryRetrieve(account.TokenEntryId);
      if(ownToken is not null)
      {
         _vault.Delete(account.TokenEntryId);
      }
      try
      {
         Persist(updated);
      }
      catch(Exception persistFailure)
      {
         if(ownToken is null)
         {
            throw;
         }
         try
         {
            _vault.Store(account.TokenEntryId, ownToken);
         }
         catch(Exception compensationFailure)
         {
            string leftover = $"account {account.Id:N} ('{account.Name}') lost its own token from the vault while accounts.json "
                + "still says it has one; edit it and enter the token again, or switch it to the default token";
            _log.Error($"Account save failed and the vault could not be restored: {leftover}.", compensationFailure);
            throw new AccountConsistencyException(
                $"Saving account '{account.Name}' failed ({persistFailure.Message}), and restoring its token "
                + $"failed too ({compensationFailure.Message}). Current state: {leftover}.",
                persistFailure, compensationFailure);
         }
         throw;
      }
      _accounts = updated;
      _log.Info($"Account '{account.Name}' {verb}; it uses the default token"
          + (ownToken is null ? "." : " (its own token was removed from the vault)."));
   }



   /// <summary>
   /// The accounts that resolve to the default token of <paramref name="place"/>
   /// (Settings shows what a removal breaks, the wizard how many share it).
   /// </summary>
   public IReadOnlyList<Account> UsingDefaultTokenOf(ForgeEndpoint place)
   {
      ArgumentNullException.ThrowIfNull(place);
      lock(_gate)
      {
         return _accounts
             .Where(a => a.UsesDefaultToken && a.TryGetEndpoint(out ForgeEndpoint? where, out _) && where.Equals(place))
             .ToList();
      }
   }



   /// <summary>
   /// Removes the account's vault token and then its metadata; an unknown id is a
   /// no-op. A vault failure changes nothing. If the metadata write fails after the
   /// token was removed, the token is stored again and the failure propagates, so a
   /// listed account never silently loses its credential and a token is never left
   /// behind without its account. See the class remarks for the full contract.
   /// </summary>
   public void Delete(Guid id)
   {
      lock(_gate)
      {
         ///////////////////////////////////////////////////////////////////////////
         // Another gclo process may have changed the file since it was last read. //
         // The change is applied to what the file holds now, under the lock every //
         // gclo process takes, so nothing another process wrote is written over.  //
         ///////////////////////////////////////////////////////////////////////////
         using FileStream _ = AcquireFileLock();
         RefreshForAChange();

         var remaining = _accounts.Where(a => a.Id != id).ToList();
         if(remaining.Count == _accounts.Count)
         {
            return;
         }
         Account removed = _accounts.First(a => a.Id == id);
         string name = removed.Name;
         Guid entry = removed.TokenEntryId;

         // Vault first: a failing Delete leaves both stores untouched. Keep the
         // token in memory only long enough to put it back if the file write fails.
         string? removedToken = _vault.TryRetrieve(entry);
         _vault.Delete(entry);

         try
         {
            Persist(remaining);
         }
         catch(Exception persistFailure)
         {
            if(removedToken is null)
            {
               throw; // there was no token to restore; the account is simply still listed
            }

            try
            {
               _vault.Store(entry, removedToken);
            }
            catch(Exception compensationFailure)
            {
               string leftover = $"account {id:N} is still listed in accounts.json but its token is gone from the vault; "
                   + "deleting it again completes the removal, or edit it and enter the token again to keep it";
               _log.Error($"Account delete failed and the token could not be restored: {leftover}.", compensationFailure);
               throw new AccountConsistencyException(
                   $"Deleting the account failed ({persistFailure.Message}), and restoring its token failed too "
                   + $"({compensationFailure.Message}). Current state: {leftover}.",
                   persistFailure, compensationFailure);
            }

            throw;
         }

         _accounts = remaining;
         _log.Info($"Account '{name}' deleted" + (removedToken is null ? " (it had no token)." : " along with its token."));
      }
   }



   /// <summary>
   /// Stamps the outcome of a finished sync onto the account and persists it; every
   /// other field is left untouched, including what another process changed on the
   /// account while its sync ran. An unknown id (account deleted while its sync ran,
   /// here or by another process) is logged and ignored: it is not brought back.
   /// </summary>
   public void RecordSyncResult(Guid id, DateTimeOffset lastSyncUtc, string summary)
   {
      lock(_gate)
      {
         ///////////////////////////////////////////////////////////////////////////
         // Another gclo process may have changed the file since it was last read. //
         // The change is applied to what the file holds now, under the lock every //
         // gclo process takes, so nothing another process wrote is written over.  //
         ///////////////////////////////////////////////////////////////////////////
         using FileStream _ = AcquireFileLock();
         RefreshForAChange();

         int index = _accounts.FindIndex(a => a.Id == id);
         if(index < 0)
         {
            _log.Error($"Cannot record a sync result: no account with id {id:N}.");
            return;
         }

         var updated = new List<Account>(_accounts)
         {
            [index] = _accounts[index] with { LastSyncUtc = lastSyncUtc, LastSyncSummary = summary },
         };
         Persist(updated);
         _accounts = updated;
         _log.Info($"Account '{updated[index].Name}': sync result recorded ({summary}).");
      }
   }



   /// <summary>
   /// What the file holds when the store is created. Never throws: a file that
   /// cannot be read leaves the store empty, says why in the log, and is left where
   /// it is (the next change reads it again and fails if it still cannot). Read
   /// without the lock: a new file is put in place in one step, so what is there is
   /// always a whole file, and a stalled process that holds the lock cannot keep
   /// another from starting.
   /// </summary>
   private List<Account> LoadAtStart()
   {
      foreach(AccountsFile file in (AccountsFile[])[_gitHubFile, _forgesFile])
      {
         try
         {
            file.Refresh();
         }
         catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
         {
            _log.Error(
                $"Could not read accounts from '{file.FilePath}' ({ex.Message}); its accounts are not shown. "
                + "The file was left as it is.",
                ex);
         }
      }
      return Union();
   }



   /// <summary>
   /// Brings what this store holds up to what the files hold now, before a change is
   /// applied. Called with the file lock held. A file that is gone or damaged is not
   /// taken for "no accounts": the change is then applied to what this store last
   /// read, and the accounts are put back here, before the change. So is an account
   /// that is in the wrong file. When that cannot be written the change fails
   /// before it touched the vault, and the change itself then has one file to write.
   /// </summary>
   private void RefreshForAChange()
   {
      foreach(AccountsFile file in (AccountsFile[])[_gitHubFile, _forgesFile])
      {
         int lastRead = file.Accounts.Count;
         if(!file.Refresh() && lastRead > 0)
         {
            _log.Error(
                $"Accounts file '{file.FilePath}' is missing or damaged; the change is applied to the {lastRead} "
                + "account(s) this session last read, which writes them back.");
         }
      }
      _accounts = Union();
      Persist(_accounts);
   }



   /// <summary>
   /// The accounts of both files as one list. An account that is in both (by its
   /// id) is the one of the file for other forges: that is the file gclo writes such
   /// an account to, and the copy in accounts.json is what an older version may
   /// have rewritten. A record of the file for other forges that names no forge
   /// (gclo writes none) is not read as a GitHub account, which would give it
   /// GitHub's listers and the default token: it names a forge nobody knows.
   /// </summary>
   private List<Account> Union()
   {
      List<Account> apart = [.. _forgesFile.Accounts.Select(a => a.ForgeName is null ? a with { ForgeName = "" } : a)];
      HashSet<Guid> theirIds = [.. apart.Select(a => a.Id)];
      return [.. _gitHubFile.Accounts.Where(a => !theirIds.Contains(a.Id)), .. apart];
   }



   /// <summary>
   /// Whether an account is kept in the file for other forges: every account that
   /// names a forge. An account that names none is a GitHub account as an older
   /// version knows it.
   /// </summary>
   private static bool IsKeptApart(Account account) => account.ForgeName is not null;



   /// <summary>
   /// Writes each account to the file it belongs in. Only a file whose accounts
   /// changed is written. The file for other forges goes first: an account that was
   /// found in accounts.json is in its own file before it leaves the other, so a
   /// failure in between leaves it in both, never in neither.
   /// </summary>
   private void Persist(List<Account> accounts)
   {
      _forgesFile.WriteIfChanged(accounts.Where(IsKeptApart).ToList());
      _gitHubFile.WriteIfChanged(accounts.Where(a => !IsKeptApart(a)).ToList());
   }



   /// <summary>
   /// What a save writes for <paramref name="account"/>, given what the file holds
   /// now. An edit of an account that another process removed since this store last
   /// read the file is refused: saving it would bring the account back, without the
   /// token that was removed with it. A sync result another process recorded after
   /// the caller's copy was read is kept.
   /// </summary>
   private Account ReconcileWithTheFile(Account account, bool knownBefore)
   {
      Account? onDisk = _accounts.FirstOrDefault(a => a.Id == account.Id);
      if(onDisk is null)
      {
         return knownBefore
             ? throw new InvalidOperationException(
                 $"Account '{account.Name}' was removed by another gclo process while it was open here; "
                 + "nothing was saved. Add it again to keep it.")
             : account;
      }

      ///////////////////////////////////////////////////////////////////////////////
      // What a later version of gclo put on the account stays, whatever record the //
      // caller built: one that was made anew (the wizard's) knows nothing of it.    //
      ///////////////////////////////////////////////////////////////////////////////
      account = account with { FieldsOfALaterVersion = onDisk.FieldsOfALaterVersion };
      return onDisk.LastSyncUtc > (account.LastSyncUtc ?? DateTimeOffset.MinValue)
          ? account with { LastSyncUtc = onDisk.LastSyncUtc, LastSyncSummary = onDisk.LastSyncSummary }
          : account;
   }



   /// <summary>
   /// What a save writes for <paramref name="account"/> as far as its forge goes, or
   /// a refusal. The forge and the server of an account say where its token is sent:
   /// </summary>
   /// <remarks>
   /// <list type="bullet">
   /// <item>They have to be usable (<see cref="Account.TryGetEndpoint"/>), and the
   /// server is written the way it was normalized.</item>
   /// <item>They are set when the account is added and cannot be changed by a save.
   /// A token that was saved for one server is not sent to another because somebody
   /// edited the address; the account is deleted and added again, with its token
   /// entered for the new place.</item>
   /// </list>
   /// </remarks>
   private Account WithItsForgeChecked(Account account)
   {
      if(!account.TryGetEndpoint(out ForgeEndpoint? endpoint, out string? reason))
      {
         throw new ArgumentException($"Account '{account.Name}' cannot be saved: {reason}.", nameof(account));
      }

      Account? onDisk = _accounts.FirstOrDefault(a => a.Id == account.Id);
      if(onDisk is null)
      {
         account = account with { Server = endpoint.Server?.AbsoluteUri };
      }
      else if(onDisk.TryGetEndpoint(out ForgeEndpoint? was, out _) && was.Equals(endpoint))
      {
         ///////////////////////////////////////////////////////////////////////////
         // The same place, however the caller spelled it. What the file says     //
         // about the place stays as it is, to the letter: the account's token is //
         // found by it (AccountTokenEntry).                                      //
         ///////////////////////////////////////////////////////////////////////////
         account = account with { ForgeName = onDisk.ForgeName, Server = onDisk.Server };
      }
      else
      {
         throw new ArgumentException(
             $"Account '{onDisk.Name}' is on {onDisk.DescribePlace()}, and that cannot be changed: its token was saved for that place. "
             + "Delete the account and add it again for the other one.",
             nameof(account));
      }
      return account;
   }



   /// <summary>
   /// Takes the lock that every gclo process takes before it changes the accounts
   /// file, waiting a short while for another process to finish. The lock is a file
   /// beside accounts.json, opened for this process alone and held until it is
   /// disposed; the file itself stays, empty.
   /// </summary>
   /// <exception cref="AccountsFileBusyException">Another gclo process kept the lock for the whole wait.</exception>
   private FileStream AcquireFileLock()
   {
      string? directory = Path.GetDirectoryName(_lockPath);
      if(!string.IsNullOrEmpty(directory))
      {
         Directory.CreateDirectory(directory);
      }

      try
      {
         return AccountsFile.WithPatience(
             () => new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None),
             _patience,
             accessDeniedIsBusy: false);
      }
      catch(IOException ex) when(AccountsFile.IsBusy(ex, accessDeniedIsBusy: false))
      {
         throw new AccountsFileBusyException(
             $"The accounts file '{_gitHubFile.FilePath}' is in use by another gclo process; nothing was changed. Try again in a moment.",
             ex);
      }
   }
}
