/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json.Nodes;
using gclo.ViewModels;


namespace gclo.Engine.Tests;


/// <summary>
/// Where accounts are kept on disk now that an account can be on another forge
/// (#204): GitHub accounts in accounts.json as ever, the others in a file of their
/// own. The tests are built around what goes wrong otherwise: a version of gclo from
/// before forges rewrites accounts.json with what it knows, and an account whose
/// forge and server it dropped would read as a GitHub account from then on. They
/// also cover what this version does with what it does not know itself (a forge or
/// a field of a later version), and that a token is found only by the account and
/// the place it was saved for.
/// </summary>
public sealed class AccountFilesTests : IDisposable
{
   private const string Server = "https://git.example.test/";

   private readonly string _root =
       Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

   private readonly InMemoryVault _vault = new();

   private readonly RecordingActivityLog _log = new();



   public void Dispose() => GitTestHelpers.TryDeleteDirectory(_root);



   private AccountsStore NewStore() => new(_vault, _root, _log);



   private string GitHubFile => Path.Combine(_root, "accounts.json");

   private string ForgesFile => Path.Combine(_root, "accounts-forges.json");



   private static Account GitHubAccount(string name = "Work") => new()
   {
      Id = Guid.NewGuid(),
      Name = name,
      Organization = "acme",
      TargetRoot = @"C:\repos",
   };



   private static Account GiteaAccount(string name = "Forge", string? server = Server)
       => GitHubAccount(name) with { Forge = ForgeKind.Gitea, Server = server };



   /// <summary>One account as a file states it, with whatever else <paramref name="more"/> says about it.</summary>
   private static string Record(Account account, string more = "")
       => $$"""
            {
              "Id": "{{account.Id}}",
              "Name": "{{account.Name}}",
              "Organization": "{{account.Organization}}",
              "TargetRoot": "C:\\repos"{{(more.Length == 0 ? "" : ",\n  " + more)}}
            }
            """;



   private void WriteFile(string path, params string[] records)
   {
      Directory.CreateDirectory(_root);
      File.WriteAllText(path, "[\n" + string.Join(",\n", records) + "\n]");
   }



   /// <summary>
   /// What a gclo from before forges does to accounts.json when it records a sync
   /// result: it reads the accounts with the fields it knows and writes them back.
   /// </summary>
   private void RewriteAsAVersionFromBeforeForgesDoes()
   {
      string[] known =
      [
          "Id", "TokenSource", "Name", "Description", "Organization", "TargetRoot", "CreateOrgSubfolder", "MaxConcurrency",
          "LastSyncUtc", "LastSyncSummary",
      ];
      JsonArray accounts = JsonNode.Parse(File.ReadAllText(GitHubFile))!.AsArray();
      var rewritten = new JsonArray();
      foreach(JsonNode? account in accounts)
      {
         var kept = new JsonObject();
         foreach(string field in known)
         {
            if(account![field] is JsonNode value)
            {
               kept[field] = value.DeepClone();
            }
         }
         kept["LastSyncSummary"] = "synced by the older version";
         rewritten.Add(kept);
      }
      File.WriteAllText(GitHubFile, rewritten.ToJsonString());
   }



   // ---------------------------------------------------------------- two files

   [Fact]
   public void AnAccountOnAnotherForge_IsKeptInAFileOfItsOwn_AndAGitHubAccountWhereItAlwaysWas()
   {
      AccountsStore store = NewStore();
      store.Save(GiteaAccount(), "gitea_token");

      // Nothing was put where an older version reads and rewrites.
      Assert.False(File.Exists(GitHubFile));
      Assert.Contains("\"Forge\": \"Gitea\"", File.ReadAllText(ForgesFile), StringComparison.Ordinal);

      store.Save(GitHubAccount(), "ghp_token");

      string gitHub = File.ReadAllText(GitHubFile);
      Assert.Contains("\"Name\": \"Work\"", gitHub, StringComparison.Ordinal);
      Assert.DoesNotContain("Forge", gitHub, StringComparison.Ordinal);
      Assert.DoesNotContain("Server", gitHub, StringComparison.Ordinal);
      Assert.DoesNotContain("\"Name\": \"Work\"", File.ReadAllText(ForgesFile), StringComparison.Ordinal);
      Assert.Equal(["Forge", "Work"], NewStore().GetAll().Select(a => a.Name), StringComparer.Ordinal);
   }



   [Fact]
   public void AnOlderVersionThatRewritesAccountsJson_LeavesAnAccountOnAnotherForgeAsItIs()
   {
      Account work = GitHubAccount();
      Account forge = GiteaAccount();
      AccountsStore store = NewStore();
      store.Save(work, "ghp_token");
      store.Save(forge, "gitea_token");

      RewriteAsAVersionFromBeforeForgesDoes();

      AccountsStore after = NewStore();
      Account stillThere = Assert.Single(after.GetAll(), a => a.Id == forge.Id);
      Assert.Equal(ForgeKind.Gitea, stillThere.Forge);
      Assert.Equal(Server, stillThere.Server);
      Assert.IsType<GiteaRepositoryLister>(AccountListers.RepositoryListerFor(stillThere));
      Assert.Equal("gitea_token", new AccountTokenResolver(_vault).TryResolve(stillThere));

      // And what the older version wrote about the GitHub account is there too.
      Assert.Equal("synced by the older version", after.FindByName("Work")!.LastSyncSummary);
      Assert.Equal("ghp_token", new AccountTokenResolver(_vault).TryResolve(after.FindByName("Work")!));
   }



   [Fact]
   public void AChange_WritesOnlyTheFileOfTheAccountItChanges()
   {
      Account work = GitHubAccount();
      Account forge = GiteaAccount();
      AccountsStore store = NewStore();
      store.Save(work, "ghp_token");
      store.Save(forge, "gitea_token");
      string gitHubBefore = File.ReadAllText(GitHubFile);
      string forgesBefore = File.ReadAllText(ForgesFile);

      // A backup is written each time a file is replaced: none so far, each file was written once.
      Assert.False(File.Exists(GitHubFile + ".bak"));
      Assert.False(File.Exists(ForgesFile + ".bak"));

      store.RecordSyncResult(forge.Id, DateTimeOffset.UtcNow, "3 synced");

      Assert.Equal(gitHubBefore, File.ReadAllText(GitHubFile));
      Assert.False(File.Exists(GitHubFile + ".bak"));
      Assert.True(File.Exists(ForgesFile + ".bak"));

      store.RecordSyncResult(work.Id, DateTimeOffset.UtcNow, "5 synced");
      string forgesAfterItsChange = File.ReadAllText(ForgesFile);
      store.Save(work with { Description = "edited" }, token: null);

      Assert.Equal(forgesAfterItsChange, File.ReadAllText(ForgesFile));
      Assert.NotEqual(forgesBefore, forgesAfterItsChange, StringComparer.Ordinal);
      Assert.True(File.Exists(GitHubFile + ".bak"));
   }



   [Fact]
   public void DeletingTheLastAccountOnAnotherForge_LeavesItsFileEmpty_AndTheOtherFileAlone()
   {
      Account work = GitHubAccount();
      Account forge = GiteaAccount();
      AccountsStore store = NewStore();
      store.Save(work, "ghp_token");
      store.Save(forge, "gitea_token");

      store.Delete(forge.Id);

      Assert.Equal("[]", File.ReadAllText(ForgesFile).Trim());
      Assert.Null(_vault.TryRetrieve(forge.TokenEntryId));
      Assert.Equal("Work", Assert.Single(NewStore().GetAll()).Name);
   }



   [Fact]
   public void AnAccountOnAnotherForge_FoundInAccountsJson_IsMovedToItsOwnFile_ByTheNextChange()
   {
      // gclo does not put it there; a person can.
      Account work = GitHubAccount();
      Account forge = GiteaAccount();
      WriteFile(GitHubFile, Record(work), Record(forge, $"\"Forge\": \"Gitea\", \"Server\": \"{Server}\""));
      AccountsStore store = NewStore();
      Assert.Equal(ForgeKind.Gitea, store.FindByName("Forge")!.Forge);

      store.RecordSyncResult(work.Id, DateTimeOffset.UtcNow, "5 synced");

      Assert.DoesNotContain("Gitea", File.ReadAllText(GitHubFile), StringComparison.Ordinal);
      Assert.Contains("\"Forge\": \"Gitea\"", File.ReadAllText(ForgesFile), StringComparison.Ordinal);
      Assert.Equal(["Forge", "Work"], NewStore().GetAll().Select(a => a.Name), StringComparer.Ordinal);
      Assert.DoesNotContain(_log.Messages, m => m.Contains("missing or damaged", StringComparison.Ordinal)); // nothing was
   }



   [Fact]
   public void AnAccountThatIsInBothFiles_IsTheOneOfItsOwnFile()
   {
      // The copy in accounts.json is what an older version could have made of it: a GitHub account.
      Account forge = GiteaAccount();
      WriteFile(GitHubFile, Record(forge));
      WriteFile(ForgesFile, Record(forge, $"\"Forge\": \"Gitea\", \"Server\": \"{Server}\""));

      Account read = Assert.Single(NewStore().GetAll());

      Assert.Equal(ForgeKind.Gitea, read.Forge);
      Assert.Equal(Server, read.Server);
   }



   [Fact]
   public void ADamagedFileOfOneKind_IsMovedAside_AndTheAccountsOfTheOtherAreStillThere()
   {
      NewStore().Save(GitHubAccount(), "ghp_token");
      File.WriteAllText(ForgesFile, "{ this is not a list of accounts");

      AccountsStore store = NewStore();

      Assert.Equal("Work", Assert.Single(store.GetAll()).Name);
      Assert.False(File.Exists(ForgesFile));
      Assert.Single(Directory.GetFiles(_root, "accounts-forges.json.corrupt-*"));
      Assert.Empty(Directory.GetFiles(_root, "accounts.json.corrupt-*"));
   }



   [Fact]
   public void AFileOfOneKindThatWentMissing_IsWrittenBack_ByAChangeToTheOther()
   {
      Account work = GitHubAccount();
      Account forge = GiteaAccount();
      AccountsStore store = NewStore();
      store.Save(work, "ghp_token");
      store.Save(forge, "gitea_token");
      File.Delete(ForgesFile);

      store.RecordSyncResult(work.Id, DateTimeOffset.UtcNow, "5 synced");

      Assert.Contains(
          _log.Messages,
          m => m.Contains("accounts-forges.json' is missing or damaged; the change is applied to the 1 account(s)", StringComparison.Ordinal));
      Assert.Equal(["Forge", "Work"], NewStore().GetAll().Select(a => a.Name), StringComparer.Ordinal);
   }



   [Fact]
   public void AFileOfOneKindThatCannotBeRead_HidesItsAccountsOnly_AndSaysWhichFile()
   {
      Account work = GitHubAccount();
      NewStore().Save(work, "ghp_token");
      NewStore().Save(GiteaAccount(), "gitea_token");

      using(new FileStream(ForgesFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
      {
         var store = new AccountsStore(_vault, _root, _log, TimeSpan.FromMilliseconds(50));

         Assert.Equal("Work", Assert.Single(store.GetAll()).Name);
         Assert.Contains(
             _log.Messages,
             m => m.Contains("accounts-forges.json'", StringComparison.Ordinal) && m.Contains("its accounts are not shown", StringComparison.Ordinal));
      }
   }



   // ---------------------------------------------------------------- what this version does not know

   [Fact]
   public void AForgeThisVersionDoesNotKnow_MakesThatAccountUnusable_NotTheFile_AndIsWrittenBackAsItWas()
   {
      Account later = GitHubAccount("Later");
      Account forge = GiteaAccount();
      WriteFile(
          ForgesFile,
          Record(later, "\"Forge\": \"GitLab\", \"Server\": \"https://gitlab.example.test/\""),
          Record(forge, $"\"Forge\": \"Gitea\", \"Server\": \"{Server}\""));
      _vault.Store(AppSettings.DefaultTokenVaultId, "ghp_default");

      AccountsStore store = NewStore();
      Account read = store.FindByName("Later")!;

      Assert.Equal(2, store.GetAll().Count);
      Assert.Empty(Directory.GetFiles(_root, "*.corrupt-*"));
      Assert.False(read.IsOnAKnownForge);
      Assert.NotEqual(ForgeKind.GitHub, read.Forge); // nothing takes it for a GitHub account
      Assert.NotEqual(ForgeKind.Gitea, read.Forge);
      Assert.False(read.TryGetEndpoint(out _, out string? reason));
      Assert.Equal("it is on a forge this version of gclo does not know", reason);
      Assert.Equal("a forge this version of gclo does not know", read.DescribePlace());
      Assert.NotEqual(read.Id, read.TokenEntryId);

      // The default token is a GitHub token, and this is not a GitHub account.
      Assert.Null(new AccountTokenResolver(_vault).TryResolve(read with { TokenSource = TokenSource.Default }));

      // A change to another account writes the file: what it says about this one stays.
      store.RecordSyncResult(forge.Id, DateTimeOffset.UtcNow, "3 synced");

      string file = File.ReadAllText(ForgesFile);
      Assert.Contains("\"Forge\": \"GitLab\"", file, StringComparison.Ordinal);
      Assert.Contains("\"Server\": \"https://gitlab.example.test/\"", file, StringComparison.Ordinal);
   }



   [Fact]
   public async Task AnAccountOnAForgeThisVersionDoesNotKnow_AsksNoServer_AndSaysWhichVersionToUse()
   {
      Account later = GitHubAccount("Later") with { ForgeName = "GitLab", Server = "https://gitlab.example.test/" };

      ForgeAccessException error = await Assert.ThrowsAsync<ForgeAccessException>(
          () => AccountListers.RepositoryListerFor(later).ListOrganizationRepositoriesAsync("acme", "a-token"));

      Assert.Equal(
          "Account 'Later' cannot be used as it is saved: it is on a forge this version of gclo does not know. "
          + "Use the version of gclo that added it.",
          error.Message);
   }



   [Theory]
   [InlineData(null, ForgeKind.GitHub)]
   [InlineData("GitHub", ForgeKind.GitHub)]
   [InlineData("github", ForgeKind.GitHub)]
   [InlineData("Gitea", ForgeKind.Gitea)]
   [InlineData("GITEA", ForgeKind.Gitea)]
   public void TheNameOfAForge_IsReadWhateverItsCase(string? stored, ForgeKind kind)
   {
      Account account = GitHubAccount() with { ForgeName = stored };

      Assert.Equal(kind, account.Forge);
      Assert.True(account.IsOnAKnownForge);
   }



   [Theory]
   [InlineData("")]
   [InlineData("1")] // not a number either: a kind is named
   [InlineData("Forgejo")]
   public void ANameThatIsNoForgeThisVersionKnows_IsNoKindAtAll(string stored)
   {
      Account account = GitHubAccount() with { ForgeName = stored };

      Assert.False(account.IsOnAKnownForge);
      Assert.False(Enum.IsDefined(account.Forge));
   }



   [Fact]
   public void TheKindOfForge_IsStoredByItsName_AndGitHubAsNothing()
   {
      Assert.Null((GitHubAccount() with { Forge = ForgeKind.GitHub }).ForgeName);
      Assert.Equal("Gitea", (GitHubAccount() with { Forge = ForgeKind.Gitea }).ForgeName);
      Assert.Null((GiteaAccount() with { Forge = ForgeKind.GitHub }).ForgeName);
   }



   [Fact]
   public void WhatALaterVersionSaysAboutAnAccount_IsWrittenBackAsItWas()
   {
      Account work = GitHubAccount();
      WriteFile(GitHubFile, Record(work, "\"Colour\": \"blue\", \"Schedule\": { \"EveryHours\": 6, \"Days\": [\"mon\", \"thu\"] }"));
      AccountsStore store = NewStore();

      store.RecordSyncResult(work.Id, DateTimeOffset.UtcNow, "5 synced");
      store.Save(store.FindByName("Work")! with { Description = "edited here" }, token: null);

      JsonNode written = JsonNode.Parse(File.ReadAllText(GitHubFile))![0]!;
      Assert.Equal("edited here", (string?)written["Description"]);
      Assert.Equal("5 synced", (string?)written["LastSyncSummary"]);
      Assert.Equal("blue", (string?)written["Colour"]);
      Assert.Equal(6, (int?)written["Schedule"]!["EveryHours"]);
      Assert.Equal("thu", (string?)written["Schedule"]!["Days"]![1]);
   }



   // ---------------------------------------------------------------- a token belongs to one account at one place

   [Fact]
   public void TheTokenEntry_IsTheAccountsIdForGitHub_AndForAnotherForgeMadeOfTheIdTheForgeAndTheServer()
   {
      Account gitHub = GitHubAccount();
      var zero = new Account { Id = Guid.Empty, Name = "z", Organization = "o", TargetRoot = "t", Forge = ForgeKind.Gitea, Server = Server };

      Assert.Equal(gitHub.Id, gitHub.TokenEntryId);

      // Pinned: tokens that are saved are found again by this value.
      Assert.Equal(new Guid("3b9b1ee2-9b5a-24bc-b069-daf504cf4a82"), zero.TokenEntryId);

      // Another account, another forge name, another server: another entry each.
      Assert.NotEqual(zero.TokenEntryId, (zero with { Id = Guid.NewGuid() }).TokenEntryId);
      Assert.NotEqual(zero.TokenEntryId, (zero with { ForgeName = "GitLab" }).TokenEntryId);
      Assert.NotEqual(zero.TokenEntryId, (zero with { Server = "https://elsewhere.example.test/" }).TokenEntryId);
      Assert.NotEqual(zero.TokenEntryId, (zero with { Server = null }).TokenEntryId);
   }



   [Fact]
   public void AServerChangedInTheFile_DoesNotGetTheTokenThatWasSavedForTheOther()
   {
      Account forge = GiteaAccount();
      NewStore().Save(forge, "gitea_token");
      File.WriteAllText(
          ForgesFile, File.ReadAllText(ForgesFile).Replace(Server, "https://elsewhere.example.test/", StringComparison.Ordinal));

      Account edited = Assert.Single(NewStore().GetAll());
      var resolver = new AccountTokenResolver(_vault);

      Assert.Equal("https://elsewhere.example.test/", edited.Server);
      Assert.True(edited.TryGetEndpoint(out _, out _)); // a usable address, and no token for it
      Assert.Null(resolver.TryResolve(edited));
      Assert.Equal("gitea_token", _vault.TryRetrieve(forge.TokenEntryId)); // where it was saved, for the place it was saved for
   }



   [Fact]
   public void ARecordThatNamesAnotherAccountsId_FindsNoTokenOfThatAccount()
   {
      Account work = GitHubAccount();
      NewStore().Save(work, "ghp_token");
      _vault.Store(AppSettings.DefaultTokenVaultId, "ghp_default");
      var resolver = new AccountTokenResolver(_vault);

      Account sameId = work with { Forge = ForgeKind.Gitea, Server = "https://elsewhere.example.test/" };
      Account asDefault = GitHubAccount() with { Id = AppSettings.DefaultTokenVaultId, Forge = ForgeKind.Gitea, Server = "https://elsewhere.example.test/" };

      Assert.Null(resolver.TryResolve(sameId));
      Assert.Null(resolver.TryResolve(asDefault));
   }



   [Fact]
   public void AServerSpelledAnotherWayInTheFile_CanStillBeEdited_AndStaysAsItIsSpelled()
   {
      Account forge = GiteaAccount();
      WriteFile(ForgesFile, Record(forge, "\"Forge\": \"gitea\", \"Server\": \"https://GIT.example.test\""));
      AccountsStore store = NewStore();

      store.Save(store.FindByName("Forge")! with { Description = "edited" }, token: null);
      store.Save(store.FindByName("Forge")! with { Server = Server, Forge = ForgeKind.Gitea, MaxConcurrency = 3 }, "gitea_token");

      Account saved = Assert.Single(NewStore().GetAll());
      Assert.Equal("edited", saved.Description);
      Assert.Equal(3, saved.MaxConcurrency);

      // To the letter: the token is found by what the file says about the place.
      Assert.Equal("gitea", saved.ForgeName);
      Assert.Equal("https://GIT.example.test", saved.Server);
      Assert.Equal("gitea_token", new AccountTokenResolver(_vault).TryResolve(saved));
   }



   // ---------------------------------------------------------------- the vault is put back under the same entry

   [Fact]
   public void ANewTokenForAnAccountOnAnotherForge_WhoseFileCannotBeWritten_PutsThePreviousTokenBack()
   {
      Account forge = GiteaAccount();
      AccountsStore store = NewStore();
      store.Save(forge, "first");
      Directory.CreateDirectory(ForgesFile + ".tmp"); // the next write of that file fails before it changes anything

      Assert.Throws<UnauthorizedAccessException>(() => store.Save(forge with { Description = "edited" }, "second"));

      Assert.Equal("first", _vault.TryRetrieve(forge.TokenEntryId));
      Assert.Null(_vault.TryRetrieve(forge.Id));
      Assert.Equal("", Assert.Single(store.GetAll()).Description);
   }



   [Fact]
   public void ANewAccountOnAnotherForge_WhoseFileCannotBeWritten_LeavesNoTokenBehind()
   {
      Account forge = GiteaAccount();
      AccountsStore store = NewStore();
      Directory.CreateDirectory(_root);
      Directory.CreateDirectory(ForgesFile + ".tmp");

      Assert.Throws<UnauthorizedAccessException>(() => store.Save(forge, "gitea_token"));

      Assert.Null(_vault.TryRetrieve(forge.TokenEntryId));
      Assert.Null(_vault.TryRetrieve(forge.Id));
      Assert.Empty(store.GetAll());
   }



   [Fact]
   public void DeletingAnAccountOnAnotherForge_WhoseFileCannotBeWritten_PutsItsTokenBack()
   {
      Account forge = GiteaAccount();
      AccountsStore store = NewStore();
      store.Save(forge, "gitea_token");
      Directory.CreateDirectory(ForgesFile + ".tmp");

      Assert.Throws<UnauthorizedAccessException>(() => store.Delete(forge.Id));

      Assert.Equal("gitea_token", _vault.TryRetrieve(forge.TokenEntryId));
      Assert.Null(_vault.TryRetrieve(forge.Id));
      Assert.Single(store.GetAll());
   }



   // ---------------------------------------------------------------- a workspace without listers of its own

   [Fact]
   public async Task AWorkspaceThatWasGivenNoListers_HasThoseOfItsAccount()
   {
      // An account that cannot be used as it is saved has listers that ask no server
      // and say why: that is what answers here, for the organizations and for the
      // repositories, and not the listers of GitHub.
      Account broken = GiteaAccount("Broken", server: "http://git.example.test");
      _vault.Store(broken.TokenEntryId, "gitea_token");
      const string Said = "Account 'Broken' cannot be used as it is saved: the server address cannot be used: "
          + "it has to begin with https://. Delete the account and add it again.";

      using var workspace = new WorkspaceViewModel(
          git: new FakeGitClient(), orgLookupDebounce: TimeSpan.FromMilliseconds(1), log: _log, account: broken, tokenVault: _vault);

      DateTime giveUp = DateTime.UtcNow.AddSeconds(10);
      while(!_log.Messages.Any(m => m.StartsWith("Organization lookup failed", StringComparison.Ordinal)) && DateTime.UtcNow < giveUp)
      {
         await Task.Delay(10);
      }
      Assert.Contains($"Organization lookup failed: {Said}", _log.Messages, StringComparer.Ordinal);

      await workspace.LoadReposCommand.ExecuteAsync(null);

      Assert.Equal(Said, workspace.LoadErrorMessage);
   }



   // ---------------------------------------------------------------- the wizard

   [Fact]
   public async Task TheWizard_NeverSendsTheDefaultTokenToAnAccountOnAnotherForge_WhateverSetTheChoice()
   {
      _vault.Store(AppSettings.DefaultTokenVaultId, "ghp_default");
      Account forge = GiteaAccount();
      AccountsStore store = NewStore();
      store.Save(forge, "gitea_token");
      var lister = new FakeOrganizationLister();
      var wizard = new AccountWizardViewModel(store, lister, new AppSettings(), forge, () => "gitea_token", defaultToken: () => "ghp_default");

      wizard.TokenChoiceIndex = 0; // the choice is not shown for this account; this sets it all the same
      Assert.True(await wizard.TryAdvanceAsync());

      Assert.False(await wizard.TryAdvanceAsync());

      Assert.Equal(2, wizard.Step);
      Assert.Equal("The default token is a GitHub token. This account needs a token of its own.", wizard.TokenError);
      Assert.Equal(0, lister.Calls);
   }
}
