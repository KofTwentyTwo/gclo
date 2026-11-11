/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.ViewModels;


namespace gclo.Engine.Tests;


/// <summary>
/// An account's forge and server: how they are stored, where the account's token is
/// kept, and the rules that keep a token on the server it was saved for. Every store
/// uses an <see cref="InMemoryVault"/> and a unique temp directory.
/// </summary>
public sealed class AccountForgeTests : IDisposable
{
   private const string Server = "https://git.example.test/";

   private readonly string _root =
       Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

   private readonly InMemoryVault _vault = new();



   public void Dispose() => GitTestHelpers.TryDeleteDirectory(_root);



   private AccountsStore NewStore() => new(_vault, _root);



   private string FileText => File.ReadAllText(Path.Combine(_root, "accounts.json"));



   private static Account GitHubAccount(string name = "Work") => new()
   {
      Id = Guid.NewGuid(),
      Name = name,
      Organization = "acme",
      TargetRoot = @"C:\repos",
   };



   private static Account GiteaAccount(string name = "Forge", string? server = Server)
       => GitHubAccount(name) with { Forge = ForgeKind.Gitea, Server = server };



   // ---------------------------------------------------------------- how it is stored

   [Fact]
   public void AGitHubAccount_IsWrittenWithoutForgeOrServer_SoOldFilesAndOldVersionsSeeNoDifference()
   {
      NewStore().Save(GitHubAccount(), "ghp_token");

      Assert.DoesNotContain("Forge", FileText, StringComparison.Ordinal);
      Assert.DoesNotContain("Server", FileText, StringComparison.Ordinal);
      Account reloaded = Assert.Single(NewStore().GetAll());
      Assert.Equal(ForgeKind.GitHub, reloaded.Forge);
      Assert.Null(reloaded.Server);
      Assert.True(reloaded.TryGetEndpoint(out ForgeEndpoint? endpoint, out _));
      Assert.Same(ForgeEndpoint.GitHub, endpoint);
   }



   [Theory]
   [InlineData("git.example.test")]
   [InlineData("https://GIT.example.test")]
   [InlineData(Server)]
   public void AGiteaAccount_IsWrittenWithItsForge_AndItsServerAsItWasNormalized(string typed)
   {
      NewStore().Save(GiteaAccount(server: typed), "gitea_token");

      Assert.Contains("\"Forge\": \"Gitea\"", FileText, StringComparison.Ordinal);
      Assert.Contains($"\"Server\": \"{Server}\"", FileText, StringComparison.Ordinal);
      Account reloaded = Assert.Single(NewStore().GetAll());
      Assert.Equal(ForgeKind.Gitea, reloaded.Forge);
      Assert.Equal(Server, reloaded.Server);
      Assert.True(reloaded.TryGetEndpoint(out ForgeEndpoint? endpoint, out _));
      Assert.Equal("git.example.test", endpoint.DisplayName);
   }



   // ---------------------------------------------------------------- where its token is kept

   [Fact]
   public void TheTokenOfAGiteaAccount_IsKeptWhereAVersionThatKnowsOnlyGitHubDoesNotLook()
   {
      Account account = GiteaAccount();

      NewStore().Save(account, "gitea_token");

      // A version from before forges reads this account as a GitHub account and
      // looks for its token under the account's id: it must find none.
      Assert.Null(_vault.TryRetrieve(account.Id));
      Assert.NotEqual(account.Id, account.TokenEntryId);
      Assert.Equal("gitea_token", _vault.TryRetrieve(account.TokenEntryId));
      Assert.Equal("gitea_token", new AccountTokenResolver(_vault).TryResolve(account));
   }



   [Fact]
   public void TheTokenEntry_IsTheAccountsIdForGitHub_AndAFixedOtherIdForAnotherForge()
   {
      Account gitHub = GitHubAccount();
      var zero = new Account { Id = Guid.Empty, Name = "z", Organization = "o", TargetRoot = "t", Forge = ForgeKind.Gitea, Server = Server };

      Assert.Equal(gitHub.Id, gitHub.TokenEntryId);

      // Pinned: tokens that are saved are found again by this value.
      Assert.Equal(new Guid("6f0c2a51-9d3e-4b77-8a14-c5e2f0b39d68"), zero.TokenEntryId);
      Assert.Equal(Guid.Empty, (zero with { Forge = ForgeKind.GitHub, Server = null }).TokenEntryId);
   }



   [Fact]
   public void Deleting_AGiteaAccount_RemovesItsToken()
   {
      Account account = GiteaAccount();
      AccountsStore store = NewStore();
      store.Save(account, "gitea_token");

      store.Delete(account.Id);

      Assert.Null(_vault.TryRetrieve(account.TokenEntryId));
      Assert.Empty(NewStore().GetAll());
   }



   [Fact]
   public void ReplacingTheTokenOfAGiteaAccount_ReplacesThatEntry()
   {
      Account account = GiteaAccount();
      AccountsStore store = NewStore();
      store.Save(account, "first");

      store.Save(account with { Description = "edited" }, "second");

      Assert.Equal("second", _vault.TryRetrieve(account.TokenEntryId));
      Assert.Null(_vault.TryRetrieve(account.Id));
   }



   // ---------------------------------------------------------------- the default token is GitHub's

   [Fact]
   public void TheDefaultToken_IsNeverGivenToAnAccountOnAnotherForge()
   {
      _vault.Store(AppSettings.DefaultTokenVaultId, "ghp_default");
      var resolver = new AccountTokenResolver(_vault);
      Account gitea = GiteaAccount() with { TokenSource = TokenSource.Default }; // as a file edited by hand could say

      Assert.Null(resolver.TryResolve(gitea));
      Assert.Contains("the default token, which is a GitHub token", resolver.MissingDescription(gitea), StringComparison.Ordinal);
      Assert.Equal("ghp_default", resolver.TryResolve(GitHubAccount() with { TokenSource = TokenSource.Default }));
   }



   [Fact]
   public void AGiteaAccount_ThatHasNoToken_IsToldWhereItsTokenWouldBe()
   {
      Account account = GiteaAccount();

      string missing = new AccountTokenResolver(_vault).MissingDescription(account);

      Assert.Contains(account.TokenEntryId.ToString("N"), missing, StringComparison.Ordinal);
      Assert.DoesNotContain(account.Id.ToString("N"), missing, StringComparison.Ordinal);
   }



   [Fact]
   public void Save_AGiteaAccountOnTheDefaultToken_IsRefused()
   {
      Account account = GiteaAccount() with { TokenSource = TokenSource.Default };

      ArgumentException ex = Assert.Throws<ArgumentException>(() => NewStore().Save(account, token: null));

      Assert.Contains("is on git.example.test; the default token is a GitHub token", ex.Message, StringComparison.Ordinal);
      Assert.Empty(NewStore().GetAll());
   }



   // ---------------------------------------------------------------- what cannot be saved

   [Theory]
   [InlineData(ForgeKind.Gitea, null, "a Gitea account needs the address of its server")]
   [InlineData(ForgeKind.Gitea, "http://git.example.test", "it has to begin with https://")]
   [InlineData(ForgeKind.Gitea, "https://user:secret@git.example.test", "it contains a user name or a password")]
   [InlineData(ForgeKind.GitHub, "https://git.example.test", "GitHub has no server address to choose")]
   public void Save_AnAccountWhoseForgeAndServerCannotBeUsed_IsRefused_AndNothingIsStored(ForgeKind forge, string? server, string said)
   {
      Account account = GitHubAccount() with { Forge = forge, Server = server };

      ArgumentException ex = Assert.Throws<ArgumentException>(() => NewStore().Save(account, "a-token"));

      Assert.Contains("cannot be saved", ex.Message, StringComparison.Ordinal);
      Assert.Contains(said, ex.Message, StringComparison.Ordinal);
      Assert.Null(_vault.TryRetrieve(account.Id));
      Assert.Null(_vault.TryRetrieve(account.TokenEntryId));
      Assert.Empty(NewStore().GetAll());
   }



   // ---------------------------------------------------------------- a token stays with its server

   [Fact]
   public void TheServerOfAnAccount_CannotBeChanged_SoItsTokenIsNotSentSomewhereElse()
   {
      Account account = GiteaAccount();
      AccountsStore store = NewStore();
      store.Save(account, "gitea_token");
      string before = FileText;

      ArgumentException ex = Assert.Throws<ArgumentException>(
          () => store.Save(account with { Server = "https://elsewhere.example.test" }, token: null));

      Assert.Contains("is on git.example.test, and that cannot be changed: its token was saved for that place", ex.Message, StringComparison.Ordinal);
      Assert.Contains("Delete the account and add it again", ex.Message, StringComparison.Ordinal);
      Assert.Equal(before, FileText);
      Assert.Equal("gitea_token", _vault.TryRetrieve(account.TokenEntryId));
   }



   [Fact]
   public void TheForgeOfAnAccount_CannotBeChanged_EvenWithANewToken()
   {
      Account account = GitHubAccount();
      AccountsStore store = NewStore();
      store.Save(account, "ghp_token");

      ArgumentException ex = Assert.Throws<ArgumentException>(
          () => store.Save(account with { Forge = ForgeKind.Gitea, Server = Server }, "gitea_token"));

      Assert.Contains("is on GitHub, and that cannot be changed", ex.Message, StringComparison.Ordinal);
      Assert.Equal("ghp_token", _vault.TryRetrieve(account.Id));
      Assert.Null(_vault.TryRetrieve((account with { Forge = ForgeKind.Gitea }).TokenEntryId));
      Assert.Equal(ForgeKind.GitHub, Assert.Single(NewStore().GetAll()).Forge);
   }



   [Fact]
   public void TheSameServer_SpelledAnotherWay_IsNoChange()
   {
      Account account = GiteaAccount(server: "git.example.test");
      AccountsStore store = NewStore();
      store.Save(account, "gitea_token");

      store.Save(account with { Server = "HTTPS://git.example.test/", Description = "edited" }, token: null);

      Account saved = Assert.Single(NewStore().GetAll());
      Assert.Equal("edited", saved.Description);
      Assert.Equal(Server, saved.Server);
   }



   [Fact]
   public void AnAccountWhoseStoredServerCannotBeUsed_IsNamedAsItIsStored_WhenAChangeIsRefused()
   {
      // A file edited by hand: the account is on a server over plain http.
      Account account = GitHubAccount("Edited");
      NewStore().Save(account, "ghp_token");
      File.WriteAllText(
          Path.Combine(_root, "accounts.json"),
          FileText.Replace("\"Name\": \"Edited\"", "\"Forge\": \"Gitea\", \"Server\": \"http://old.example.test\", \"Name\": \"Edited\"", StringComparison.Ordinal));
      AccountsStore store = NewStore();
      Account onDisk = Assert.Single(store.GetAll());
      Assert.False(onDisk.TryGetEndpoint(out _, out string? reason));
      Assert.Contains("https://", reason, StringComparison.Ordinal);

      ArgumentException ex = Assert.Throws<ArgumentException>(
          () => store.Save(onDisk with { Server = Server }, "gitea_token"));

      Assert.Contains("is on Gitea, at an address that cannot be used, and that cannot be changed", ex.Message, StringComparison.Ordinal);
      Assert.DoesNotContain("old.example.test", ex.Message, StringComparison.Ordinal); // not repeated: it could hold a password
   }



   // ---------------------------------------------------------------- the listers of an account

   [Fact]
   public void AnAccountsListers_AreThoseOfItsForge()
   {
      Assert.IsType<GitHubRepositoryLister>(AccountListers.RepositoryListerFor(GitHubAccount()));
      Assert.IsType<GitHubOrganizationLister>(AccountListers.OrganizationListerFor(GitHubAccount()));
      Assert.IsType<GiteaRepositoryLister>(AccountListers.RepositoryListerFor(GiteaAccount()));
      Assert.IsType<GiteaOrganizationLister>(AccountListers.OrganizationListerFor(GiteaAccount()));
      Assert.Throws<ArgumentNullException>(() => AccountListers.RepositoryListerFor(null!));
      Assert.Throws<ArgumentNullException>(() => AccountListers.OrganizationListerFor(null!));
   }



   [Fact]
   public async Task AnAccountThatCannotBeUsedAsItIsSaved_AsksNoServerAnything_AndSaysWhy()
   {
      Account broken = GiteaAccount("Broken", server: "http://git.example.test");

      ForgeAccessException fromRepositories = await Assert.ThrowsAsync<ForgeAccessException>(
          () => AccountListers.RepositoryListerFor(broken).ListOrganizationRepositoriesAsync("acme", "a-token"));
      ForgeAccessException fromOrganizations = await Assert.ThrowsAsync<ForgeAccessException>(
          () => AccountListers.OrganizationListerFor(broken).ListOrganizationsAsync("a-token"));

      Assert.Equal(ForgeAccessKind.NotFound, fromRepositories.Kind);
      Assert.Equal(
          "Account 'Broken' cannot be used as it is saved: the server address cannot be used: "
          + "it has to begin with https://. Delete the account and add it again.",
          fromRepositories.Message);
      Assert.Equal(fromRepositories.Message, fromOrganizations.Message);
      Assert.DoesNotContain("a-token", fromRepositories.Message, StringComparison.Ordinal);
   }



   // ---------------------------------------------------------------- the wizard

   [Fact]
   public async Task TheWizard_EditingAGiteaAccount_KeepsItWhereItIs_AndOffersNoDefaultToken()
   {
      _vault.Store(AppSettings.DefaultTokenVaultId, "ghp_default");
      Account account = GiteaAccount();
      AccountsStore store = NewStore();
      store.Save(account, "gitea_token");
      var wizard = new AccountWizardViewModel(
          store, new FakeOrganizationLister(), new AppSettings(), account, () => "gitea_token", defaultToken: () => "ghp_default");

      // The default token is a GitHub token: it is not on offer here.
      Assert.False(wizard.OffersTokenChoice);
      Assert.False(wizard.UseDefaultToken);

      wizard.Description = "edited in the wizard";
      while(wizard.Step < 4)
      {
         Assert.True(await wizard.TryAdvanceAsync(), $"stopped at step {wizard.Step}");
      }
      await wizard.SaveAsync();

      Account saved = Assert.Single(NewStore().GetAll());
      Assert.Equal("edited in the wizard", saved.Description);
      Assert.Equal(ForgeKind.Gitea, saved.Forge);
      Assert.Equal(Server, saved.Server);
      Assert.Equal("gitea_token", _vault.TryRetrieve(saved.TokenEntryId));
   }



   [Fact]
   public void TheWizard_ForAnAccountOnAnotherForgeThatSaysDefaultToken_DoesNotPreselectIt()
   {
      _vault.Store(AppSettings.DefaultTokenVaultId, "ghp_default");
      Account edited = GiteaAccount() with { TokenSource = TokenSource.Default }; // as a file edited by hand could say

      var wizard = new AccountWizardViewModel(
          NewStore(), new FakeOrganizationLister(), new AppSettings(), edited, () => null, defaultToken: () => "ghp_default");

      Assert.False(wizard.UseDefaultToken);
      Assert.False(wizard.OffersTokenChoice);
   }
}
