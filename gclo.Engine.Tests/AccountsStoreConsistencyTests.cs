/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.ViewModels;


namespace gclo.Engine.Tests;


/// <summary>
/// Contract tests for the two-store consistency rules of <see cref="AccountsStore"/>
/// (see its class remarks): the order of vault and metadata writes, the compensating
/// step at every partial-failure point, what is left behind when compensation fails
/// too, that a plain retry reconciles every such state, and that no path ever logs
/// or surfaces a token.
/// </summary>
public sealed class AccountsStoreConsistencyTests : IDisposable
{
   private const string OldToken = "ghp_OLD_TOKEN_VALUE";

   private const string NewToken = "ghp_NEW_TOKEN_VALUE";

   private readonly string _root =
       Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

   private readonly FaultableVault _vault = new();

   private readonly RecordingLog _log = new();



   public void Dispose()
   {
      UnblockMetadataWrites();
      GitTestHelpers.TryDeleteDirectory(_root);
   }



   private AccountsStore NewStore() => new(_vault, _root, _log);



   private static Account MakeAccount(string name) => new()
   {
      Id = Guid.NewGuid(),
      Name = name,
      Organization = "acme",
      TargetRoot = @"C:\repos",
   };



   /// <summary>
   /// Makes the next metadata write fail before it changes anything: the store writes
   /// to accounts.json.tmp first, and a directory squatting on that name makes the
   /// FileStream creation throw UnauthorizedAccessException.
   /// </summary>
   private void BlockMetadataWrites()
       => Directory.CreateDirectory(Path.Combine(_root, "accounts.json.tmp"));



   private void UnblockMetadataWrites()
   {
      string squatter = Path.Combine(_root, "accounts.json.tmp");
      if(Directory.Exists(squatter))
      {
         Directory.Delete(squatter);
      }
   }



   private IReadOnlyList<Account> OnDisk() => new AccountsStore(new InMemoryVault(), _root).GetAll();



   // ---------------------------------------------------------------- save: vault write fails

   [Fact]
   public void Save_NewAccount_VaultWriteFails_ChangesNothing()
   {
      AccountsStore store = NewStore();
      Account account = MakeAccount("Work");
      _vault.StoreFailure = new InvalidOperationException("credential manager locked");

      InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => store.Save(account, NewToken));

      Assert.Equal("credential manager locked", ex.Message);
      Assert.Empty(store.GetAll());
      Assert.False(File.Exists(Path.Combine(_root, "accounts.json")), "metadata must not be written before the vault");
      Assert.Null(_vault.TryRetrieve(account.Id));
   }



   [Fact]
   public void Save_UpdateWithNewToken_VaultWriteFails_KeepsPriorMetadataAndPriorToken()
   {
      AccountsStore store = NewStore();
      Account account = MakeAccount("Work");
      store.Save(account, OldToken);
      _vault.StoreFailure = new InvalidOperationException("credential manager locked");

      Assert.Throws<InvalidOperationException>(
          () => store.Save(account with { Description = "edited" }, NewToken));

      // The replacement failed, so the account keeps the credential that worked.
      Assert.Equal(OldToken, _vault.TryRetrieve(account.Id));
      Assert.Equal("", Assert.Single(store.GetAll()).Description);
      Assert.Equal("", Assert.Single(OnDisk()).Description);
   }



   // ---------------------------------------------------------------- save: metadata write fails

   [Fact]
   public void Save_NewAccount_MetadataWriteFails_RemovesTheVaultEntryAgain()
   {
      AccountsStore store = NewStore();
      Account account = MakeAccount("Work");
      BlockMetadataWrites();

      Assert.Throws<UnauthorizedAccessException>(() => store.Save(account, NewToken));

      // No vault entry may point at an account that was never saved.
      Assert.Null(_vault.TryRetrieve(account.Id));
      Assert.Empty(store.GetAll());
      Assert.Equal(1, _vault.DeleteCalls);
   }



   [Fact]
   public void Save_UpdateWithNewToken_MetadataWriteFails_RestoresThePreviousToken()
   {
      AccountsStore store = NewStore();
      Account account = MakeAccount("Work");
      store.Save(account, OldToken);
      BlockMetadataWrites();

      Assert.Throws<UnauthorizedAccessException>(() => store.Save(account with { Description = "edited" }, NewToken));

      Assert.Equal(OldToken, _vault.TryRetrieve(account.Id));
      Assert.Equal("", Assert.Single(store.GetAll()).Description);
      Assert.Equal("", Assert.Single(OnDisk()).Description);
   }



   [Fact]
   public void Save_WithoutToken_MetadataWriteFails_NeverTouchesTheVault()
   {
      AccountsStore store = NewStore();
      Account account = MakeAccount("Work");
      store.Save(account, OldToken);
      _vault.ResetCounters();
      BlockMetadataWrites();

      Assert.Throws<UnauthorizedAccessException>(() => store.Save(account with { Description = "edited" }, token: null));

      Assert.Equal(0, _vault.StoreCalls + _vault.DeleteCalls + _vault.RetrieveCalls);
      Assert.Equal(OldToken, _vault.TryRetrieve(account.Id));
   }



   // ---------------------------------------------------------------- save: compensation fails

   [Fact]
   public void Save_NewAccount_MetadataAndCompensationFail_SurfacesBothWithoutTheToken()
   {
      AccountsStore store = NewStore();
      Account account = MakeAccount("Work");
      BlockMetadataWrites();
      _vault.DeleteFailure = new InvalidOperationException("delete refused");

      AccountConsistencyException ex = Assert.Throws<AccountConsistencyException>(() => store.Save(account, NewToken));

      Assert.IsType<UnauthorizedAccessException>(ex.InnerException);
      Assert.Equal("delete refused", ex.CompensationException.Message);
      Assert.Contains("not in accounts.json", ex.Message);
      Assert.Contains(account.Id.ToString("N"), ex.Message);
      AssertNoTokenLeaked(ex.Message);
      Assert.Equal(NewToken, _vault.TryRetrieve(account.Id)); // the documented leftover
      Assert.Empty(store.GetAll());

      (string Message, Exception? Exception) logged = Assert.Single(_log.Errors);
      Assert.Contains("could not be restored", logged.Message);
      AssertNoTokenLeaked(logged.Message);
   }



   [Fact]
   public void Save_Update_MetadataAndCompensationFail_SurfacesBothWithoutTheToken()
   {
      AccountsStore store = NewStore();
      Account account = MakeAccount("Work");
      store.Save(account, OldToken);
      BlockMetadataWrites();
      _vault.ResetCounters();
      _vault.StoreFailureFromCall = 2; // the replacement write succeeds, the restore does not
      _vault.StoreFailure = new InvalidOperationException("restore refused");

      AccountConsistencyException ex = Assert.Throws<AccountConsistencyException>(
          () => store.Save(account with { Description = "edited" }, NewToken));

      Assert.Equal("restore refused", ex.CompensationException.Message);
      Assert.Contains("previous metadata", ex.Message);
      AssertNoTokenLeaked(ex.Message);
      Assert.Equal(NewToken, _vault.TryRetrieve(account.Id)); // the documented leftover
      Assert.Equal("", Assert.Single(store.GetAll()).Description);
      AssertNoTokenLeaked(Assert.Single(_log.Errors).Message);
   }



   // ---------------------------------------------------------------- delete

   [Fact]
   public void Delete_VaultDeleteFails_ChangesNothing()
   {
      AccountsStore store = NewStore();
      Account account = MakeAccount("Work");
      store.Save(account, OldToken);
      _vault.DeleteFailure = new InvalidOperationException("delete refused");

      Assert.Throws<InvalidOperationException>(() => store.Delete(account.Id));

      Assert.Equal(OldToken, _vault.TryRetrieve(account.Id));
      Assert.Single(store.GetAll());
      Assert.Single(OnDisk());
   }



   [Fact]
   public void Delete_MetadataWriteFails_RestoresTheTokenAndKeepsTheAccount()
   {
      AccountsStore store = NewStore();
      Account account = MakeAccount("Work");
      store.Save(account, OldToken);
      BlockMetadataWrites();

      Assert.Throws<UnauthorizedAccessException>(() => store.Delete(account.Id));

      // Not orphaned, not lost: the account is still listed AND still has its token.
      Assert.Equal(OldToken, _vault.TryRetrieve(account.Id));
      Assert.Single(store.GetAll());
      Assert.Single(OnDisk());
   }



   [Fact]
   public void Delete_AccountWithoutToken_MetadataWriteFails_HasNothingToRestore()
   {
      AccountsStore store = NewStore();
      Account account = MakeAccount("Work");
      store.Save(account, token: null);
      _vault.ResetCounters();
      BlockMetadataWrites();

      Assert.Throws<UnauthorizedAccessException>(() => store.Delete(account.Id));

      Assert.Equal(0, _vault.StoreCalls);
      Assert.Single(store.GetAll());
   }



   [Fact]
   public void Delete_MetadataAndCompensationFail_SurfacesBothWithoutTheToken()
   {
      AccountsStore store = NewStore();
      Account account = MakeAccount("Work");
      store.Save(account, OldToken);
      BlockMetadataWrites();
      _vault.StoreFailure = new InvalidOperationException("restore refused");

      AccountConsistencyException ex = Assert.Throws<AccountConsistencyException>(() => store.Delete(account.Id));

      Assert.IsType<UnauthorizedAccessException>(ex.InnerException);
      Assert.Equal("restore refused", ex.CompensationException.Message);
      Assert.Contains("still listed", ex.Message);
      AssertNoTokenLeaked(ex.Message);
      Assert.Null(_vault.TryRetrieve(account.Id)); // the documented leftover
      Assert.Single(store.GetAll());
      AssertNoTokenLeaked(Assert.Single(_log.Errors).Message);
   }



   // ---------------------------------------------------------------- retry reconciles every leftover

   [Fact]
   public void Save_RetryAfterMetadataFailure_Succeeds()
   {
      AccountsStore store = NewStore();
      Account account = MakeAccount("Work");
      BlockMetadataWrites();
      Assert.Throws<UnauthorizedAccessException>(() => store.Save(account, NewToken));
      UnblockMetadataWrites();

      store.Save(account, NewToken);

      Assert.Equal(NewToken, _vault.TryRetrieve(account.Id));
      Assert.Equal(account, Assert.Single(OnDisk()));
   }



   [Fact]
   public void Save_RetryAfterOrphanedVaultEntry_Reconciles()
   {
      AccountsStore store = NewStore();
      Account account = MakeAccount("Work");
      BlockMetadataWrites();
      _vault.DeleteFailure = new InvalidOperationException("delete refused");
      Assert.Throws<AccountConsistencyException>(() => store.Save(account, NewToken));
      UnblockMetadataWrites();
      _vault.DeleteFailure = null;

      store.Save(account, NewToken);

      Assert.Equal(NewToken, _vault.TryRetrieve(account.Id));
      Assert.Equal(account, Assert.Single(store.GetAll()));
   }



   [Fact]
   public void Delete_RetryAfterTokenRestoreFailed_CompletesTheRemoval()
   {
      AccountsStore store = NewStore();
      Account account = MakeAccount("Work");
      store.Save(account, OldToken);
      BlockMetadataWrites();
      _vault.StoreFailure = new InvalidOperationException("restore refused");
      Assert.Throws<AccountConsistencyException>(() => store.Delete(account.Id));
      UnblockMetadataWrites();
      _vault.StoreFailure = null;

      store.Delete(account.Id); // the vault entry is already gone; that must be fine

      Assert.Empty(store.GetAll());
      Assert.Empty(OnDisk());
      Assert.Null(_vault.TryRetrieve(account.Id));
   }



   [Fact]
   public void Delete_RetryAfterMetadataFailure_Succeeds()
   {
      AccountsStore store = NewStore();
      Account account = MakeAccount("Work");
      store.Save(account, OldToken);
      BlockMetadataWrites();
      Assert.Throws<UnauthorizedAccessException>(() => store.Delete(account.Id));
      UnblockMetadataWrites();

      store.Delete(account.Id);

      Assert.Empty(store.GetAll());
      Assert.Null(_vault.TryRetrieve(account.Id));
   }



   // ---------------------------------------------------------------- helpers

   private static void AssertNoTokenLeaked(string text)
   {
      Assert.DoesNotContain(OldToken, text);
      Assert.DoesNotContain(NewToken, text);
   }



   /// <summary>
   /// An <see cref="ITokenVault"/> over <see cref="InMemoryVault"/> whose Store and
   /// Delete can be made to throw (from a given call number on) without changing
   /// state — the contract a real secret store keeps on failure.
   /// </summary>
   private sealed class FaultableVault : ITokenVault
   {
      private readonly InMemoryVault _inner = new();

      public Exception? StoreFailure { get; set; }

      public int StoreFailureFromCall { get; set; } = 1;

      public Exception? DeleteFailure { get; set; }

      public int StoreCalls { get; private set; }

      public int DeleteCalls { get; private set; }

      public int RetrieveCalls { get; private set; }



      public void ResetCounters() => StoreCalls = DeleteCalls = RetrieveCalls = 0;



      public void Store(Guid accountId, string token)
      {
         StoreCalls++;
         if(StoreFailure is not null && StoreCalls >= StoreFailureFromCall)
         {
            throw StoreFailure;
         }
         _inner.Store(accountId, token);
      }



      public string? TryRetrieve(Guid accountId)
      {
         RetrieveCalls++;
         return _inner.TryRetrieve(accountId);
      }



      public void Delete(Guid accountId)
      {
         DeleteCalls++;
         if(DeleteFailure is not null)
         {
            throw DeleteFailure;
         }
         _inner.Delete(accountId);
      }
   }



   private sealed class RecordingLog : IActivityLog
   {
      public List<(string Message, Exception? Exception)> Errors { get; } = [];



      public void Info(string message)
      {
      }



      public void Error(string message, Exception? exception = null) => Errors.Add((message, exception));



      public string LogDirectory => "";



      public string CurrentLogFilePath => "";
   }
}
