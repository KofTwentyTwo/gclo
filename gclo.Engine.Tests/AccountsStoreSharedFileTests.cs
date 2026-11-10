/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.ViewModels;


namespace gclo.Engine.Tests;


/// <summary>
/// The desktop app and the CLI share accounts.json, and each may run more than once.
/// Two stores over one folder stand for two processes here: a change made through
/// one must survive the next change made through the other, and a file that is in
/// use is waited for, never taken for a corrupt one (#155).
/// </summary>
public sealed class AccountsStoreSharedFileTests : IDisposable
{
   private static readonly TimeSpan s_long = TimeSpan.FromSeconds(60);

   private static readonly TimeSpan s_short = TimeSpan.FromMilliseconds(300);

   private readonly string _root =
       Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

   /// <summary>One vault for both stores, as Credential Manager is one for both processes.</summary>
   private readonly InMemoryVault _vault = new();

   private readonly RecordingActivityLog _log = new();



   public void Dispose() => GitTestHelpers.TryDeleteDirectory(_root);



   private string FilePath => Path.Combine(_root, "accounts.json");



   private string LockPath => Path.Combine(_root, "accounts.json.lock");



   private AccountsStore NewStore(TimeSpan? patience = null) => new(_vault, _root, _log, patience ?? s_long);



   private static Account MakeAccount(string name) => new()
   {
      Id = Guid.NewGuid(),
      Name = name,
      Organization = "acme",
      TargetRoot = @"C:\repos",
   };



   private IReadOnlyList<string> NamesOnDisk()
       => new AccountsStore(new InMemoryVault(), _root).GetAll().Select(a => a.Name).ToList();



   private IReadOnlyList<string> ErrorsLogged()
       => _log.Entries.Where(e => string.Equals(e.Level, "ERROR", StringComparison.Ordinal)).Select(e => e.Message).ToList();



   // ---------------------------------------------------------------- one change at a time, on what the file holds now

   [Fact]
   public void AnAccountAddedByTheOtherProcess_SurvivesThisProcessesNextChange()
   {
      AccountsStore app = NewStore();
      AccountsStore cli = NewStore(); // both started before anything was added

      cli.Save(MakeAccount("From the CLI"), "ghp_cli_token");
      app.Save(MakeAccount("From the app"), null);

      Assert.Equal(["From the app", "From the CLI"], NamesOnDisk(), StringComparer.Ordinal);
      // The store that made the later change has seen the earlier one.
      Assert.Equal(["From the app", "From the CLI"], app.GetAll().Select(a => a.Name), StringComparer.Ordinal);
   }



   [Fact]
   public void RecordingASyncResult_KeepsWhatTheOtherProcessAddedAndChanged_AndItsTokens()
   {
      Account work = MakeAccount("Work");
      NewStore().Save(work, "ghp_work_token");
      AccountsStore app = NewStore();
      AccountsStore cli = NewStore();
      Account added = MakeAccount("Added meanwhile");

      cli.Save(added, "ghp_added_token");
      cli.Save(work with { Description = "edited by the CLI" }, null);
      app.RecordSyncResult(work.Id, new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero), "42 done");

      IReadOnlyList<Account> onDisk = new AccountsStore(_vault, _root).GetAll();
      Assert.Equal(["Added meanwhile", "Work"], onDisk.Select(a => a.Name), StringComparer.Ordinal);
      Account recorded = onDisk.Single(a => a.Id == work.Id);
      Assert.Equal("edited by the CLI", recorded.Description); // the other process's change
      Assert.Equal("42 done", recorded.LastSyncSummary);       // and this one's
      Assert.Equal("ghp_added_token", _vault.TryRetrieve(added.Id)); // no token without its account
      Assert.Equal("ghp_work_token", _vault.TryRetrieve(work.Id));
   }



   [Fact]
   public void RecordingASyncResult_ForAnAccountTheOtherProcessDeleted_DoesNotBringItBack()
   {
      Account work = MakeAccount("Work");
      Account other = MakeAccount("Other");
      AccountsStore setup = NewStore();
      setup.Save(work, "ghp_work_token");
      setup.Save(other, null);
      AccountsStore app = NewStore();
      AccountsStore cli = NewStore();

      cli.Delete(work.Id);
      app.RecordSyncResult(work.Id, DateTimeOffset.UtcNow, "done");

      Assert.Equal(["Other"], NamesOnDisk(), StringComparer.Ordinal);
      Assert.Null(_vault.TryRetrieve(work.Id));
      Assert.Contains(ErrorsLogged(), e => e.Contains("no account with id", StringComparison.Ordinal));
      Assert.Equal(["Other"], app.GetAll().Select(a => a.Name), StringComparer.Ordinal);
   }



   [Fact]
   public void Save_UnderANameTheOtherProcessTookMeanwhile_IsRefused_AndWritesNothing()
   {
      AccountsStore app = NewStore();
      AccountsStore cli = NewStore();
      cli.Save(MakeAccount("Work"), null);
      Account clash = MakeAccount("work");

      ArgumentException ex = Assert.Throws<ArgumentException>(() => app.Save(clash, "ghp_clash_token"));

      Assert.Contains("already exists", ex.Message);
      Assert.Equal(["Work"], NamesOnDisk(), StringComparer.Ordinal);
      Assert.Null(_vault.TryRetrieve(clash.Id));
   }



   [Fact]
   public void Delete_OfAnAccountTheOtherProcessAlreadyDeleted_ChangesNothing_AndKeepsTheRest()
   {
      Account work = MakeAccount("Work");
      AccountsStore setup = NewStore();
      setup.Save(work, null);
      AccountsStore app = NewStore();
      AccountsStore cli = NewStore();

      cli.Delete(work.Id);
      cli.Save(MakeAccount("Added meanwhile"), null);
      app.Delete(work.Id);

      Assert.Equal(["Added meanwhile"], NamesOnDisk(), StringComparer.Ordinal);
   }



   [Fact]
   public void ManyChangesThroughTwoStoresAtOnce_AreAllKept()
   {
      AccountsStore app = NewStore();
      AccountsStore cli = NewStore();
      const int PerStore = 25;

      Parallel.Invoke(
          () =>
          {
             for(int i = 0; i < PerStore; i++)
             {
                app.Save(MakeAccount($"app-{i:D2}"), null);
             }
          },
          () =>
          {
             for(int i = 0; i < PerStore; i++)
             {
                cli.Save(MakeAccount($"cli-{i:D2}"), null);
             }
          });

      Assert.Equal(2 * PerStore, NamesOnDisk().Count);
   }



   // ---------------------------------------------------------------- the lock

   [Fact]
   public async Task AChange_WhileTheOtherProcessHoldsTheLock_WaitsForIt()
   {
      AccountsStore store = NewStore();
      Directory.CreateDirectory(_root);
      Task save;

      using(new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
      {
         save = Task.Run(() => store.Save(MakeAccount("Waited"), null));
         Task finished = await Task.WhenAny(save, Task.Delay(TimeSpan.FromMilliseconds(200)));
         Assert.NotSame(save, finished); // still waiting for the other process
         Assert.False(File.Exists(FilePath));
      }
      await save.WaitAsync(s_long);

      Assert.Equal(["Waited"], NamesOnDisk(), StringComparer.Ordinal);
   }



   [Fact]
   public void AChange_WhenTheLockStaysHeld_Fails_AndChangesNothing()
   {
      Account existing = MakeAccount("Existing");
      NewStore().Save(existing, "ghp_existing_token");
      AccountsStore store = NewStore(s_short);
      string before = File.ReadAllText(FilePath);
      Account added = MakeAccount("Never saved");

      using(new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
      {
         AccountsFileBusyException save = Assert.Throws<AccountsFileBusyException>(() => store.Save(added, "ghp_never_stored"));
         Assert.Contains("in use by another gclo process", save.Message);
         Assert.Throws<AccountsFileBusyException>(() => store.Delete(existing.Id));
         Assert.Throws<AccountsFileBusyException>(() => store.RecordSyncResult(existing.Id, DateTimeOffset.UtcNow, "done"));
      }

      Assert.Equal(before, File.ReadAllText(FilePath));
      Assert.Null(_vault.TryRetrieve(added.Id));
      Assert.Equal("ghp_existing_token", _vault.TryRetrieve(existing.Id));
      Assert.Equal(["Existing"], store.GetAll().Select(a => a.Name), StringComparer.Ordinal);
   }



   [Fact]
   public void Construction_DoesNotWaitForTheLock_SoAStalledProcessCannotKeepAnotherFromStarting()
   {
      NewStore().Save(MakeAccount("Existing"), null);

      AccountsStore store;
      var clock = System.Diagnostics.Stopwatch.StartNew();
      using(new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
      {
         store = NewStore(); // a patience of a minute, which it does not use
      }

      Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"construction waited {clock.Elapsed} for the lock");
      Assert.Equal(["Existing"], store.GetAll().Select(a => a.Name), StringComparer.Ordinal);
      Assert.Empty(ErrorsLogged());
   }



   [Fact]
   public void ALockThatCannotBeOpenedAtAll_IsNotTakenForABusyOne_AndDoesNotHideTheAccounts()
   {
      NewStore().Save(MakeAccount("Existing"), null);
      File.Delete(LockPath);
      Directory.CreateDirectory(LockPath); // a folder where the lock file belongs

      AccountsStore store = NewStore();
      Assert.Equal(["Existing"], store.GetAll().Select(a => a.Name), StringComparer.Ordinal);

      var clock = System.Diagnostics.Stopwatch.StartNew();
      Exception ex = Assert.ThrowsAny<Exception>(() => store.Save(MakeAccount("Never saved"), null));

      Assert.IsNotType<AccountsFileBusyException>(ex);
      Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"waited {clock.Elapsed} for something that does not pass");
      Assert.Equal(["Existing"], NamesOnDisk(), StringComparer.Ordinal);
   }



   [Fact]
   public void TheLockFile_StaysBesideTheAccounts_Empty_AndIsNotCreatedByLookingAtNothing()
   {
      _ = NewStore().GetAll();
      Assert.False(Directory.Exists(_root), "looking at a folder with no accounts creates nothing");

      NewStore().Save(MakeAccount("First"), null);

      Assert.True(File.Exists(LockPath));
      Assert.Equal(0, new FileInfo(LockPath).Length);
   }



   // ---------------------------------------------------------------- a file that is in use is not a corrupt file

   [Fact]
   public async Task Construction_WhenTheFileIsInUseForAMoment_TriesAgain()
   {
      if(!OperatingSystem.IsWindows())
      {
         return; // file sharing is what keeps a reader out
      }
      NewStore().Save(MakeAccount("Existing"), null);
      Task<AccountsStore> construction;

      using(new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
      {
         construction = Task.Run(() => NewStore());
         Task finished = await Task.WhenAny(construction, Task.Delay(TimeSpan.FromMilliseconds(200)));
         Assert.NotSame(construction, finished); // still waiting for the file
      }
      AccountsStore store = await construction.WaitAsync(s_long);

      Assert.Equal(["Existing"], store.GetAll().Select(a => a.Name), StringComparer.Ordinal);
      Assert.Empty(ErrorsLogged());
   }



   [Fact]
   public void WhenTheFileStaysUnreadable_ItIsNotMovedAside_AndAChangeFailsWithoutWriting()
   {
      if(!OperatingSystem.IsWindows())
      {
         return; // file sharing is what keeps a reader out
      }
      NewStore().Save(MakeAccount("Existing"), null);
      string before = File.ReadAllText(FilePath);
      Account added = MakeAccount("Never saved");

      AccountsStore store;
      using(new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
      {
         store = NewStore(s_short);
         Assert.Empty(store.GetAll());
         Assert.Throws<IOException>(() => store.Save(added, "ghp_never_stored"));
      }

      Assert.Equal(before, File.ReadAllText(FilePath));
      Assert.Empty(Directory.GetFiles(_root, "accounts.json.corrupt-*"));
      Assert.Null(_vault.TryRetrieve(added.Id));
      Assert.Contains(ErrorsLogged(), e => e.Contains("Could not read accounts", StringComparison.Ordinal));
      Assert.Equal(["Existing"], NewStore().GetAll().Select(a => a.Name), StringComparer.Ordinal);
   }



   [Fact]
   public void ContentThatIsNotAListOfAccounts_IsStillMovedAside_AlsoWhenAChangeFindsIt()
   {
      AccountsStore store = NewStore();
      store.Save(MakeAccount("Existing"), null);
      File.WriteAllText(FilePath, "{ not json"); // damaged after the store read it

      store.Save(MakeAccount("Fresh"), null);

      string aside = Assert.Single(Directory.GetFiles(_root, "accounts.json.corrupt-*"));
      Assert.Equal("{ not json", File.ReadAllText(aside));
      // The change did not start over from nothing: what the store last read is written back with it.
      Assert.Equal(["Existing", "Fresh"], NamesOnDisk(), StringComparer.Ordinal);
      Assert.Contains(ErrorsLogged(), e => e.Contains("not a valid list of accounts", StringComparison.Ordinal));
      Assert.Contains(ErrorsLogged(), e => e.Contains("missing or damaged", StringComparison.Ordinal));
   }



   // ---------------------------------------------------------------- two processes and one account

   [Fact]
   public void AnEditOfAnAccountTheOtherProcessRemoved_IsRefused_NotBroughtBackWithoutItsToken()
   {
      Account work = MakeAccount("Work");
      NewStore().Save(work, "ghp_work_token");
      AccountsStore app = NewStore(); // has Work loaded
      AccountsStore cli = NewStore();
      cli.Delete(work.Id);

      InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
          () => app.Save(work with { Description = "edited in the app" }, null));

      Assert.Contains("was removed by another gclo process", ex.Message);
      Assert.Empty(NamesOnDisk());
      Assert.Null(_vault.TryRetrieve(work.Id));
      Assert.Empty(app.GetAll()); // and the store now knows it is gone

      // Adding it again is a new decision, and works.
      app.Save(work, "ghp_new_token");
      Assert.Equal(["Work"], NamesOnDisk(), StringComparer.Ordinal);
   }



   [Fact]
   public void AnEditFromAnOlderCopy_KeepsTheSyncResultTheOtherProcessRecordedSince()
   {
      Account work = MakeAccount("Work");
      NewStore().Save(work, null);
      AccountsStore app = NewStore();
      AccountsStore cli = NewStore();
      var recorded = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
      cli.RecordSyncResult(work.Id, recorded, "42 done");

      app.Save(work with { Description = "edited in the app" }, null); // 'work' was read before the sync

      Account onDisk = Assert.Single(new AccountsStore(_vault, _root).GetAll());
      Assert.Equal("edited in the app", onDisk.Description);
      Assert.Equal(recorded, onDisk.LastSyncUtc);
      Assert.Equal("42 done", onDisk.LastSyncSummary);
   }



   [Fact]
   public void AnEditThatCarriesANewerSyncResult_KeepsItsOwn()
   {
      Account work = MakeAccount("Work") with
      {
         LastSyncUtc = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
         LastSyncSummary = "older",
      };
      AccountsStore store = NewStore();
      store.Save(work, null);
      var newer = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

      store.Save(work with { LastSyncUtc = newer, LastSyncSummary = "newer" }, null);

      Account onDisk = Assert.Single(new AccountsStore(_vault, _root).GetAll());
      Assert.Equal(newer, onDisk.LastSyncUtc);
      Assert.Equal("newer", onDisk.LastSyncSummary);
   }



   // ---------------------------------------------------------------- a file that is gone is not "no accounts"

   [Fact]
   public void AChange_ThatFindsTheFileGone_IsAppliedToWhatTheStoreLastRead_WhichPutsTheAccountsBack()
   {
      Account kept = MakeAccount("Kept");
      Account removed = MakeAccount("Removed");
      AccountsStore store = NewStore();
      store.Save(kept, "ghp_kept_token");
      store.Save(removed, "ghp_removed_token");
      File.Delete(FilePath);

      store.Delete(removed.Id);

      Assert.Equal(["Kept"], NamesOnDisk(), StringComparer.Ordinal);
      Assert.Null(_vault.TryRetrieve(removed.Id)); // the delete did what it said, also for the token
      Assert.Equal("ghp_kept_token", _vault.TryRetrieve(kept.Id));
      Assert.Contains(ErrorsLogged(), e => e.Contains("missing or damaged", StringComparison.Ordinal));
   }



   [Fact]
   public void AFirstChange_WhereThereNeverWasAFile_SaysNothingAboutDamage()
   {
      NewStore().Save(MakeAccount("First"), null);

      Assert.Empty(ErrorsLogged());
   }



   [Fact]
   public async Task WhileChangesAreBeingSaved_TheFileIsNeverAbsent()
   {
      // A process that starts while another saves must find the accounts, not "no file yet".
      AccountsStore store = NewStore();
      store.Save(MakeAccount("First"), null);
      using var stop = new CancellationTokenSource();
      int absences = 0;
      int looks = 0;
      var watcher = Task.Run(() =>
      {
         while(!stop.IsCancellationRequested)
         {
            looks++;
            if(!File.Exists(FilePath))
            {
               absences++;
            }
         }
      });

      for(int i = 0; i < 150; i++)
      {
         store.RecordSyncResult(store.GetAll()[0].Id, DateTimeOffset.UtcNow, $"run {i}");
      }
      stop.Cancel();
      await watcher;

      Assert.True(looks > 0);
      Assert.Equal(0, absences);
   }



   // ---------------------------------------------------------------- files that will not change by waiting

   [Fact]
   public void AReadOnlyAccountsFile_FailsAtOnce_AndSaysSo()
   {
      AccountsStore store = NewStore(); // a patience of a minute, which it does not use
      Account existing = MakeAccount("Existing");
      store.Save(existing, null);
      File.SetAttributes(FilePath, FileAttributes.ReadOnly);
      Account added = MakeAccount("Never saved");
      try
      {
         var clock = System.Diagnostics.Stopwatch.StartNew();
         UnauthorizedAccessException ex = Assert.Throws<UnauthorizedAccessException>(() => store.Save(added, "ghp_never_kept"));

         Assert.Contains("is read-only", ex.Message);
         Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"waited {clock.Elapsed} for a file that stays read-only");
         Assert.Null(_vault.TryRetrieve(added.Id));
      }
      finally
      {
         File.SetAttributes(FilePath, FileAttributes.Normal);
      }
      Assert.Equal(["Existing"], NamesOnDisk(), StringComparer.Ordinal);
   }



   [Fact]
   public void ABackupThatIsReadOnly_IsReplaced_AndOneThatCannotBeWritten_DoesNotStopTheChange()
   {
      AccountsStore store = NewStore();
      store.Save(MakeAccount("First"), null);
      store.Save(MakeAccount("Second"), null);
      string backup = FilePath + ".bak";
      File.SetAttributes(backup, FileAttributes.ReadOnly);

      store.Save(MakeAccount("Third"), null);
      Assert.Contains("Second", File.ReadAllText(backup)); // the file as it was before this change
      Assert.Empty(ErrorsLogged());

      File.SetAttributes(backup, FileAttributes.Normal);
      File.Delete(backup);
      Directory.CreateDirectory(backup); // a folder where the backup belongs
      store.Save(MakeAccount("Fourth"), null);

      Assert.Equal(["First", "Fourth", "Second", "Third"], NamesOnDisk(), StringComparer.Ordinal);
      Assert.Contains(ErrorsLogged(), e => e.Contains("Could not write the backup", StringComparison.Ordinal));
   }



   // ---------------------------------------------------------------- the swap, when something else holds the live file

   /// <summary>Holds the file the way a scanner or a viewer does: it can be read, not replaced.</summary>
   private FileStream HoldLiveFileAgainstReplacement()
       => new(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);



   [Fact]
   public async Task AChange_WhenTheLiveFileIsHeldForAMoment_TriesTheSwapAgain()
   {
      if(!OperatingSystem.IsWindows())
      {
         return; // an open file can be replaced elsewhere
      }
      AccountsStore store = NewStore();
      store.Save(MakeAccount("Existing"), null);
      Task save;

      using(HoldLiveFileAgainstReplacement())
      {
         save = Task.Run(() => store.Save(MakeAccount("Added"), null));
         Task finished = await Task.WhenAny(save, Task.Delay(TimeSpan.FromMilliseconds(200)));
         Assert.NotSame(save, finished); // still trying the swap
      }
      await save.WaitAsync(s_long);

      Assert.Equal(["Added", "Existing"], NamesOnDisk(), StringComparer.Ordinal);
   }



   [Fact]
   public void AChange_WhenTheLiveFileStaysHeld_Fails_AndTheStoreStillSaysWhatIsOnDisk()
   {
      if(!OperatingSystem.IsWindows())
      {
         return; // an open file can be replaced elsewhere
      }
      AccountsStore store = NewStore(s_short);
      store.Save(MakeAccount("Existing"), null);
      Account added = MakeAccount("Never saved");

      using(HoldLiveFileAgainstReplacement())
      {
         Exception ex = Assert.ThrowsAny<Exception>(() => store.Save(added, "ghp_never_kept"));
         Assert.True(ex is IOException or UnauthorizedAccessException, ex.GetType().Name);
      }

      Assert.Equal(["Existing"], NamesOnDisk(), StringComparer.Ordinal);
      Assert.Equal(["Existing"], store.GetAll().Select(a => a.Name), StringComparer.Ordinal);
      Assert.Null(_vault.TryRetrieve(added.Id)); // the vault write was undone
   }
}
