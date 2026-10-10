/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

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
/// is tolerant (a missing or corrupt file yields an empty list).
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
/// <para>In-memory state changes only after both stores succeeded. Nothing here logs a token.</para>
/// </remarks>
public sealed class AccountsStore
{
   private readonly ITokenVault _vault;

   private readonly IActivityLog _log;

   private readonly string _filePath;

   private readonly Lock _gate = new();

   private List<Account> _accounts;



   /// <summary>
   /// Loads existing accounts from <paramref name="directory"/> (default:
   /// <see cref="GcloPaths.DataRoot"/>). A missing file simply means no accounts yet;
   /// a corrupt or unreadable one is logged (pass the app's <paramref name="log"/> to
   /// surface that) and treated as empty rather than blocking startup.
   /// </summary>
   public AccountsStore(ITokenVault vault, string? directory = null, IActivityLog? log = null)
   {
      ArgumentNullException.ThrowIfNull(vault);
      _vault = vault;
      _log = log ?? new NullActivityLog();
      directory ??= GcloPaths.DataRoot;
      _filePath = Path.Combine(directory, "accounts.json");
      _accounts = Load();
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
   /// immediately. Throws <see cref="ArgumentException"/> when another account
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
   /// other field is left untouched. An unknown id (account deleted while its sync
   /// ran) is logged and ignored.
   /// </summary>
   public void RecordSyncResult(Guid id, DateTimeOffset lastSyncUtc, string summary)
   {
      lock(_gate)
      {
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



   private List<Account> Load()
   {
      try
      {
         if(!File.Exists(_filePath))
         {
            return [];
         }

         List<Account>? loaded = JsonSerializer.Deserialize(
             File.ReadAllText(_filePath), AccountsJsonContext.Default.ListAccount);
         if(loaded is not null)
         {
            return loaded;
         }
         _log.Error($"Accounts file '{_filePath}' deserialized to null; starting with no accounts.");
         PreserveCorruptFile();
      }
      catch(Exception ex)
      {
         _log.Error($"Failed to load accounts from '{_filePath}'; starting with no accounts.", ex);
         PreserveCorruptFile();
      }

      return [];
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

      if(File.Exists(_filePath))
      {
         File.Replace(tempPath, _filePath, _filePath + ".bak");
      }
      else
      {
         File.Move(tempPath, _filePath);
      }
   }
}
