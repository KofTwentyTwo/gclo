/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Text.Json;
using gclo.Engine;


namespace gclo.ViewModels;


/// <summary>
/// One file of accounts on disk, as <see cref="AccountsStore"/> keeps them: how it is
/// read (tolerant of a missing or damaged file, patient with a busy one) and how it
/// is written (in one step, with a backup). It remembers what the file last held,
/// so a file that goes missing or is damaged can be written back, and so a change
/// that does not concern this file leaves it alone.
/// </summary>
/// <remarks>
/// A file that cannot be read is not a corrupt file. Only content that is not valid
/// JSON is moved aside. A file that is in use is tried again for a short while; one
/// that stays unreadable is left exactly where it is, and the read throws.
/// </remarks>
internal sealed class AccountsFile
{
   private readonly string _filePath;

   private readonly IActivityLog _log;

   private readonly TimeSpan _patience;

   private static readonly TimeSpan s_pause = TimeSpan.FromMilliseconds(15);



   /// <summary>
   /// The file at <paramref name="filePath"/>. <paramref name="patience"/> is how long
   /// a file that something else holds for a moment is tried again.
   /// </summary>
   public AccountsFile(string filePath, IActivityLog log, TimeSpan patience)
   {
      _filePath = filePath;
      _log = log;
      _patience = patience;
   }



   /// <summary>The file's full path.</summary>
   public string FilePath => _filePath;

   /// <summary>
   /// What the file held when it was last read or written. Kept when a later read
   /// finds the file missing or damaged: it is what is then written back.
   /// </summary>
   public IReadOnlyList<Account> Accounts { get; private set; } = [];

   /// <summary>Whether the last read found a list of accounts in the file, or the last write put one there.</summary>
   public bool IsThere { get; private set; }

   private TimeSpan FilePatience => _patience;



   /// <summary>
   /// Reads the file again. True when it holds a list of accounts, which is then
   /// what <see cref="Accounts"/> gives. False when the file is missing or its
   /// content is not a list of accounts (it is then moved aside): <see cref="Accounts"/>
   /// stays what it was. A file that cannot be read at all throws, and is not touched.
   /// </summary>
   public bool Refresh()
   {
      List<Account>? read = ReadFile();
      IsThere = read is not null;
      if(read is not null)
      {
         Accounts = read;
      }
      return IsThere;
   }



   /// <summary>
   /// Writes <paramref name="accounts"/> when the file does not hold exactly that
   /// already: a file that is there is rewritten only for a change, and one that is
   /// missing or damaged is written when there is an account to put in it.
   /// </summary>
   public void WriteIfChanged(List<Account> accounts)
   {
      bool needed = IsThere ? !accounts.SequenceEqual(Accounts) : accounts.Count > 0;
      if(needed)
      {
         Write(accounts);
      }
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



   /// <summary>Runs <paramref name="action"/>, trying again for <paramref name="patience"/> as long as the file is busy.</summary>
   internal static T WithPatience<T>(Func<T> action, TimeSpan patience, bool accessDeniedIsBusy)
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
   internal static bool IsBusy(Exception exception, bool accessDeniedIsBusy)
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



   /// <summary>
   /// Puts <paramref name="accounts"/> in the file, in one step: the file is never
   /// absent and never half written. A failure leaves the file, and what this object
   /// holds, as they were.
   /// </summary>
   private void Write(List<Account> accounts)
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
      Accounts = accounts;
      IsThere = true;
   }



   /// <summary>
   /// Copies the file that is about to be replaced to a .bak file beside it. Best effort:
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
