/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.ViewModels;


namespace gclo.Engine.Tests;


/// <summary>
/// Tests for <see cref="AccountsStore"/>: JSON round-trips across store instances,
/// case-insensitive lookups and name uniqueness, vault coordination, and tolerance
/// of a corrupt accounts file. Every store uses an <see cref="InMemoryVault"/> and a
/// unique temp directory.
/// </summary>
public sealed class AccountsStoreTests : IDisposable
{
   private readonly string _root =
       Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

   private readonly InMemoryVault _vault = new();



   public void Dispose() => GitTestHelpers.TryDeleteDirectory(_root);



   private AccountsStore NewStore() => new(_vault, _root);



   private static Account MakeAccount(string name) => new()
   {
      Id = Guid.NewGuid(),
      Name = name,
      Organization = "acme",
      TargetRoot = @"C:\repos",
   };



   // ---------------------------------------------------------------- default token (#102)

   [Fact]
   public void Save_DefaultTokenAccount_WritesNoOwnEntry_AndRoundTripsTheSource()
   {
      Account account = MakeAccount("Shared") with { TokenSource = TokenSource.Default };

      NewStore().Save(account, token: null);

      Assert.Null(_vault.TryRetrieve(account.Id));
      Account reloaded = Assert.Single(NewStore().GetAll());
      Assert.Equal(TokenSource.Default, reloaded.TokenSource);
      Assert.True(reloaded.UsesDefaultToken);
      Assert.Contains("\"TokenSource\": \"Default\"", File.ReadAllText(Path.Combine(_root, "accounts.json")));
   }



   [Fact]
   public void Save_OwnTokenAccount_OmitsTheSourceFromTheFile_SoOldFilesStayIdentical()
   {
      NewStore().Save(MakeAccount("Work"), "ghp_own");

      string json = File.ReadAllText(Path.Combine(_root, "accounts.json"));
      Assert.DoesNotContain("TokenSource", json);
      Assert.Equal(TokenSource.Own, Assert.Single(NewStore().GetAll()).TokenSource);
   }



   [Fact]
   public void Load_FileWithoutTheSourceField_ReadsAsOwn()
   {
      Directory.CreateDirectory(_root);
      var id = Guid.NewGuid();
      File.WriteAllText(
          Path.Combine(_root, "accounts.json"),
          $$"""[{"Id":"{{id}}","Name":"Old","Organization":"acme","TargetRoot":"C:\\repos"}]""");

      Account loaded = Assert.Single(NewStore().GetAll());

      Assert.Equal(TokenSource.Own, loaded.TokenSource);
      Assert.False(loaded.UsesDefaultToken);
   }



   [Fact]
   public void Save_DefaultTokenAccount_WithAToken_IsRejected()
   {
      Account account = MakeAccount("Shared") with { TokenSource = TokenSource.Default };

      ArgumentException ex = Assert.Throws<ArgumentException>(() => NewStore().Save(account, "ghp_stray"));

      Assert.Contains("default token", ex.Message);
      Assert.Empty(NewStore().GetAll());
      Assert.Null(_vault.TryRetrieve(account.Id));
   }



   [Fact]
   public void Save_SwitchingToTheDefaultToken_RemovesTheOwnEntry()
   {
      AccountsStore store = NewStore();
      Account account = MakeAccount("Work");
      store.Save(account, "ghp_own");
      Assert.Equal("ghp_own", _vault.TryRetrieve(account.Id));

      store.Save(account with { TokenSource = TokenSource.Default }, token: null);

      Assert.Null(_vault.TryRetrieve(account.Id));
      Assert.True(Assert.Single(store.GetAll()).UsesDefaultToken);
   }



   [Fact]
   public void Save_SwitchingBackToAnOwnToken_StoresIt()
   {
      AccountsStore store = NewStore();
      Account account = MakeAccount("Work") with { TokenSource = TokenSource.Default };
      store.Save(account, token: null);

      store.Save(account with { TokenSource = TokenSource.Own }, "ghp_mine");

      Assert.Equal("ghp_mine", _vault.TryRetrieve(account.Id));
      Assert.False(Assert.Single(store.GetAll()).UsesDefaultToken);
   }



   [Fact]
   public void CountUsingDefaultToken_CountsOnlyDefaultTokenAccounts()
   {
      AccountsStore store = NewStore();
      Assert.Equal(0, store.CountUsingDefaultToken());
      store.Save(MakeAccount("A") with { TokenSource = TokenSource.Default }, null);
      store.Save(MakeAccount("B") with { TokenSource = TokenSource.Default }, null);
      store.Save(MakeAccount("C"), "ghp_c");

      Assert.Equal(2, store.CountUsingDefaultToken());
      Assert.Equal(2, NewStore().CountUsingDefaultToken());
   }

   // ---------------------------------------------------------------- persistence

   [Fact]
   public void Crud_RoundTrips_AcrossStoreInstances()
   {
      Account account = MakeAccount("Work") with
      {
         Description = "primary org",
         CreateOrgSubfolder = true,
         MaxConcurrency = 4,
      };

      NewStore().Save(account, token: null);
      Assert.Equal(account, Assert.Single(NewStore().GetAll()));

      Account edited = account with { Name = "Work (new)", Organization = "acme-2" };
      NewStore().Save(edited, token: null);
      Assert.Equal(edited, Assert.Single(NewStore().GetAll()));

      NewStore().Delete(account.Id);
      Assert.Empty(NewStore().GetAll());
   }



   [Fact]
   public void FreshDirectory_YieldsEmptyList()
   {
      Assert.Empty(NewStore().GetAll());
   }



   // ---------------------------------------------------------------- queries

   [Fact]
   public void GetAll_SortsByName_CaseInsensitively()
   {
      AccountsStore store = NewStore();
      store.Save(MakeAccount("Charlie"), null);
      store.Save(MakeAccount("beta"), null);
      store.Save(MakeAccount("Alpha"), null);

      // Ordinal (case-sensitive) order would be Alpha, Charlie, beta.
      Assert.Equal(
          new[] { "Alpha", "beta", "Charlie" },
          store.GetAll().Select(a => a.Name), StringComparer.Ordinal);
   }



   [Fact]
   public void FindByName_MatchesCaseInsensitively()
   {
      AccountsStore store = NewStore();
      Account account = MakeAccount("Work");
      store.Save(account, null);

      Assert.Equal(account, store.FindByName("WORK"));
      Assert.Equal(account, store.FindByName("work"));
      Assert.Null(store.FindByName("missing"));
   }



   // ---------------------------------------------------------------- name uniqueness

   [Theory]
   [InlineData("Work")]
   [InlineData("WORK")]
   [InlineData("work")]
   public void Save_DuplicateNameOnDifferentId_ThrowsAndPersistsNothing(string duplicateName)
   {
      NewStore().Save(MakeAccount("Work"), null);
      Account duplicate = MakeAccount(duplicateName);

      Assert.Throws<ArgumentException>(() => NewStore().Save(duplicate, "secret"));

      Account survivor = Assert.Single(NewStore().GetAll());
      Assert.Equal("Work", survivor.Name);
      Assert.Null(_vault.TryRetrieve(duplicate.Id));
   }



   [Fact]
   public void Save_SameIdWithSameName_IsAnUpdateNotADuplicate()
   {
      Account account = MakeAccount("Work");
      AccountsStore store = NewStore();
      store.Save(account, null);

      store.Save(account with { Description = "edited" }, null);

      Assert.Equal("edited", Assert.Single(store.GetAll()).Description);
   }



   // ---------------------------------------------------------------- vault coordination

   [Fact]
   public void Save_WithToken_StoresItInTheVault()
   {
      Account account = MakeAccount("Work");

      NewStore().Save(account, "ghp_secret");

      Assert.Equal("ghp_secret", _vault.TryRetrieve(account.Id));
   }



   [Fact]
   public void Save_WithNullToken_LeavesVaultUntouched()
   {
      Account account = MakeAccount("Work");
      AccountsStore store = NewStore();
      store.Save(account, "original");

      store.Save(account with { Description = "edited" }, null);

      Assert.Equal("original", _vault.TryRetrieve(account.Id));
   }



   [Fact]
   public void Delete_RemovesMetadataAndVaultEntry_AndIsIdempotent()
   {
      Account account = MakeAccount("Work");
      AccountsStore store = NewStore();
      store.Save(account, "secret");

      store.Delete(account.Id);

      Assert.Empty(store.GetAll());
      Assert.Empty(NewStore().GetAll());
      Assert.Null(_vault.TryRetrieve(account.Id));

      store.Delete(account.Id); // deleting again must not throw
   }



   // ---------------------------------------------------------------- sync results

   [Fact]
   public void RecordSyncResult_UpdatesOnlySyncFields_AndPersists()
   {
      Account account = MakeAccount("Work") with { Description = "keep me", MaxConcurrency = 4 };
      NewStore().Save(account, null);
      var finished = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

      NewStore().RecordSyncResult(account.Id, finished, "12 cloned, 3 updated, 0 failed");

      Account updated = Assert.Single(NewStore().GetAll());
      Assert.Equal(
          account with { LastSyncUtc = finished, LastSyncSummary = "12 cloned, 3 updated, 0 failed" },
          updated);
   }



   // ---------------------------------------------------------------- load tolerance

   [Fact]
   public void CorruptFile_YieldsEmptyList_AndSubsequentSaveWorks()
   {
      Directory.CreateDirectory(_root);
      File.WriteAllText(Path.Combine(_root, "accounts.json"), "{ this is not json !!");

      AccountsStore store = NewStore();
      Assert.Empty(store.GetAll());

      Account account = MakeAccount("Recovered");
      store.Save(account, null);
      Assert.Equal(account, Assert.Single(NewStore().GetAll()));
   }



   [Fact]
   public void Load_CorruptFile_IsPreservedAside_SoSaveCannotClobberIt()
   {
      Directory.CreateDirectory(_root);
      string path = Path.Combine(_root, "accounts.json");
      File.WriteAllText(path, "{ not valid json");

      AccountsStore store = NewStore();
      Assert.Empty(store.GetAll());

      // The unreadable original must survive as evidence for token re-association.
      string? preserved = Directory.GetFiles(_root, "accounts.json.corrupt-*").SingleOrDefault();
      Assert.NotNull(preserved);
      Assert.Equal("{ not valid json", File.ReadAllText(preserved));

      store.Save(MakeAccount("Fresh"), null);
      Assert.Equal("{ not valid json", File.ReadAllText(preserved)); // untouched
   }



   [Fact]
   public void Persist_ReplacesAtomically_KeepingABackupOfThePreviousFile()
   {
      AccountsStore store = NewStore();
      store.Save(MakeAccount("First"), null);
      store.Save(MakeAccount("Second"), null);

      // The atomic File.Replace keeps the prior generation as .bak, and no
      // temp file is left behind.
      Assert.True(File.Exists(Path.Combine(_root, "accounts.json.bak")));
      Assert.False(File.Exists(Path.Combine(_root, "accounts.json.tmp")));
      Assert.Equal(2, NewStore().GetAll().Count);
   }
}
