/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Text.Json;
using gclo.Engine;


namespace gclo.ViewModels;


/// <summary>
/// Persists account profiles as JSON at accounts.json under
/// <see cref="GcloPaths.DataRoot"/> (default %LOCALAPPDATA%\KofTwentyTwo\gclo) and their
/// tokens in an <see cref="ITokenVault"/>; the file holds metadata only and never a
/// token. Unlike <see cref="AppSettings"/>, accounts are primary user data, so write
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

   private readonly string _filePath;

   /// <summary>How long the lock another gclo process holds is waited for.</summary>
   private static readonly TimeSpan s_defaultPatience = TimeSpan.FromSeconds(5);

   /// <summary>
   /// The share of the patience spent on a file that something outside gclo holds for
   /// a moment (reading it, or swapping the new one into place). Kept well below the
   /// wait for the lock, so a process that holds the lock while it waits here does
   /// not outlast the patience of the ones waiting for it.
   /// </summary>
   private const int FilePatienceDivisor = 5;

   private static readonly TimeSpan s_pause = TimeSpan.FromMilliseconds(15);

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
      _filePath = Path.Combine(directory, "accounts.json");
      _patience = patience;
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



   /// <summary>The account whose name matches case-insensitively, or null.</summary>
   public Account? FindByName(string name)
   {
      lock(_gate)
      {
         return _accounts.FirstOrDefault(
             a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
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
         string? previousToken = _vault.TryRetrieve(account.Id);
         _vault.Store(account.Id, token);

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
                  _vault.Delete(account.Id);
               }
               else
               {
                  _vault.Store(account.Id, previousToken);
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
      string? ownToken = _vault.TryRetrieve(account.Id);
      if(ownToken is not null)
      {
         _vault.Delete(account.Id);
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
            _vault.Store(account.Id, ownToken);
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



   /// <summary>How many accounts resolve to the default token (Settings shows what a removal breaks).</summary>
   public int CountUsingDefaultToken()
   {
      lock(_gate)
      {
         return _accounts.Count(a => a.UsesDefaultToken);
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
         string name = _accounts.First(a => a.Id == id).Name;

         // Vault first: a failing Delete leaves both stores untouched. Keep the
         // token in memory only long enough to put it back if the file write fails.
         string? removedToken = _vault.TryRetrieve(id);
         _vault.Delete(id);

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
               _vault.Store(id, removedToken);
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
      try
      {
         return ReadFile() ?? [];
      }
      catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
      {
         _log.Error(
             $"Could not read accounts from '{_filePath}' ({ex.Message}); no accounts are shown. "
             + "The file was left as it is.",
             ex);
         return [];
      }
   }



   /// <summary>
   /// Brings what this store holds up to what the file holds now, before a change is
   /// applied. Called with the file lock held. A file that is gone or damaged is not
   /// taken for "no accounts": the change is then applied to what this store last
   /// read, and writing it puts the accounts back.
   /// </summary>
   private void RefreshForAChange()
   {
      List<Account>? current = ReadFile();
      if(current is not null)
      {
         _accounts = current;
      }
      else if(_accounts.Count > 0)
      {
         _log.Error(
             $"Accounts file '{_filePath}' is missing or damaged; the change is applied to the {_accounts.Count} "
             + "account(s) this session last read, which writes them back.");
      }
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

      return onDisk.LastSyncUtc > (account.LastSyncUtc ?? DateTimeOffset.MinValue)
          ? account with { LastSyncUtc = onDisk.LastSyncUtc, LastSyncSummary = onDisk.LastSyncSummary }
          : account;
   }



   /// <summary>
   /// The accounts the file holds now, or null when it holds none that can be used:
   /// the file is missing, or its content is not a list of accounts (it is then moved
   /// aside). A file that cannot be read at all throws, and is not touched.
   /// </summary>
   private List<Account>? ReadFile()
   {
      string json;
      try
      {
         json = WithPatience(() => File.ReadAllText(_filePath), FilePatience, accessDeniedIsBusy: false);
      }
      catch(Exception ex) when(ex is FileNotFoundException or DirectoryNotFoundException)
      {
         return null;
      }

      try
      {
         List<Account>? loaded = JsonSerializer.Deserialize(json, AccountsJsonContext.Default.ListAccount);
         if(loaded is not null)
         {
            return loaded;
         }
         _log.Error($"Accounts file '{_filePath}' deserialized to null; starting with no accounts.");
      }
      catch(Exception ex)
      {
         ///////////////////////////////////////////////////////////////////////////
         // The text was read; it is what it says that cannot be used. That, and  //
         // only that, is a corrupt file.                                          //
         ///////////////////////////////////////////////////////////////////////////
         _log.Error($"Accounts file '{_filePath}' is not a valid list of accounts; starting with no accounts.", ex);
      }

      PreserveCorruptFile();
      return null;
   }



   private TimeSpan FilePatience => _patience / FilePatienceDivisor;



   /// <summary>
   /// Takes the lock that every gclo process takes before it changes the accounts
   /// file, waiting a short while for another process to finish. The lock is a file
   /// beside accounts.json, opened for this process alone and held until it is
   /// disposed; the file itself stays, empty.
   /// </summary>
   /// <exception cref="AccountsFileBusyException">Another gclo process kept the lock for the whole wait.</exception>
   private FileStream AcquireFileLock()
   {
      string? directory = Path.GetDirectoryName(_filePath);
      if(!string.IsNullOrEmpty(directory))
      {
         Directory.CreateDirectory(directory);
      }

      try
      {
         return WithPatience(
             () => new FileStream(_filePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None),
             _patience,
             accessDeniedIsBusy: false);
      }
      catch(IOException ex) when(IsBusy(ex, accessDeniedIsBusy: false))
      {
         throw new AccountsFileBusyException(
             $"The accounts file '{_filePath}' is in use by another gclo process; nothing was changed. Try again in a moment.",
             ex);
      }
   }



   /// <summary>Runs <paramref name="action"/>, trying again for <paramref name="patience"/> as long as the file is busy.</summary>
   private static T WithPatience<T>(Func<T> action, TimeSpan patience, bool accessDeniedIsBusy)
   {
      long started = Stopwatch.GetTimestamp();
      while(true)
      {
         try
         {
            return action();
         }
         catch(Exception ex) when(IsBusy(ex, accessDeniedIsBusy) && Stopwatch.GetElapsedTime(started) < patience)
         {
            Thread.Sleep(s_pause);
         }
      }
   }



   /// <summary>
   /// True for a failure that means "in use right now", which passes: on Windows a
   /// sharing or lock violation, and, where the operation reports a busy target that
   /// way (replacing a file somebody has open), a refused access. A missing file, a
   /// full disk, or a path that is too long is never that, and is not waited for.
   /// </summary>
   private static bool IsBusy(Exception exception, bool accessDeniedIsBusy)
   {
      const int SharingViolation = 32;
      const int LockViolation = 33;
      if(exception is UnauthorizedAccessException)
      {
         return accessDeniedIsBusy;
      }

      return exception is IOException and not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException)
          && (!OperatingSystem.IsWindows() || (exception.HResult & 0xFFFF) is SharingViolation or LockViolation);
   }



   /// <summary>
   /// Moves an unreadable accounts file aside before the empty in-memory list can be
   /// persisted over it: the metadata is what associates vault entries with accounts,
   /// so clobbering a recoverable file would orphan every stored token.
   /// </summary>
   private void PreserveCorruptFile()
   {
      try
      {
         if(File.Exists(_filePath))
         {
            string aside = $"{_filePath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            File.Move(_filePath, aside, overwrite: true);
            _log.Error($"Preserved unreadable accounts file as '{aside}'.");
         }
      }
      catch(Exception ex)
      {
         _log.Error("Could not preserve the unreadable accounts file.", ex);
      }
   }



   private void Persist(List<Account> accounts)
   {
      string? directory = Path.GetDirectoryName(_filePath);
      if(!string.IsNullOrEmpty(directory))
      {
         Directory.CreateDirectory(directory);
      }

      // Atomic replace: a crash or full disk mid-write must never truncate the live
      // file — corrupted metadata orphans every vault token (accounts are found by
      // Guid, and recreated accounts mint new ones).
      string json = JsonSerializer.Serialize(accounts, AccountsJsonContext.Default.ListAccount);
      string tempPath = _filePath + ".tmp";
      using(var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
      using(var writer = new StreamWriter(stream))
      {
         writer.Write(json);
         writer.Flush();
         stream.Flush(flushToDisk: true);
      }

      KeepABackup();

      ///////////////////////////////////////////////////////////////////////////////
      // A file nobody may write to stays that way however long it is tried: say   //
      // so at once instead of taking the refusal for a file that is busy.          //
      ///////////////////////////////////////////////////////////////////////////////
      if(File.Exists(_filePath) && File.GetAttributes(_filePath).HasFlag(FileAttributes.ReadOnly))
      {
         throw new UnauthorizedAccessException($"The accounts file '{_filePath}' is read-only; nothing was changed.");
      }

      ///////////////////////////////////////////////////////////////////////////////
      // One step puts the new file in place of the old: the name is never absent, //
      // so a process that reads without the lock always finds a whole file.        //
      // Something outside gclo (a scanner, a backup, a viewer) can hold the live   //
      // file for a moment; the step is then refused, and tried again for a while.  //
      ///////////////////////////////////////////////////////////////////////////////
      WithPatience(
          () =>
          {
             File.Move(tempPath, _filePath, overwrite: true);
             return true;
          },
          FilePatience,
          accessDeniedIsBusy: true);
   }



   /// <summary>
   /// Copies the file that is about to be replaced to accounts.json.bak. Best effort:
   /// the backup is a convenience for a person, and failing to write it must not
   /// stop the change it is a backup for.
   /// </summary>
   private void KeepABackup()
   {
      try
      {
         if(File.Exists(_filePath))
         {
            string backup = _filePath + ".bak";
            if(File.Exists(backup))
            {
               File.SetAttributes(backup, FileAttributes.Normal);
            }
            File.Copy(_filePath, backup, overwrite: true);
         }
      }
      catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
      {
         _log.Error($"Could not write the backup '{_filePath}.bak' ({ex.Message}); the change goes ahead without it.", ex);
      }
   }
}
