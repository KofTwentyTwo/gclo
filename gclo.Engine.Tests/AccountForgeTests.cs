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

   /// <summary>The file of the accounts that are not on GitHub (see <see cref="AccountFilesTests"/>).</summary>
   private string ForgesFileText => File.ReadAllText(Path.Combine(_root, "accounts-forges.json"));



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

      Assert.Contains("\"Forge\": \"Gitea\"", ForgesFileText, StringComparison.Ordinal);
      Assert.Contains($"\"Server\": \"{Server}\"", ForgesFileText, StringComparison.Ordinal);
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
   public void TheDefaultTokenOfGitHub_IsNeverGivenToAnAccountOnAGiteaServer()
   {
      _vault.Store(AppSettings.DefaultTokenVaultId, "ghp_default");
      var resolver = new AccountTokenResolver(_vault);
      Account gitea = GiteaAccount() with { TokenSource = TokenSource.Default };

      // A default token belongs to one place: the server has none, and GitHub's is not it.
      Assert.Null(resolver.TryResolve(gitea));
      Assert.Equal(
          "uses the default token for git.example.test, but none is saved for it (desktop app: Settings)",
          resolver.MissingDescription(gitea));
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
   public void Save_AGiteaAccountOnTheDefaultTokenOfItsServer_HasNoTokenOfItsOwn()
   {
      Account account = GiteaAccount() with { TokenSource = TokenSource.Default };

      NewStore().Save(account, token: null);

      Account saved = Assert.Single(NewStore().GetAll());
      Assert.Equal(TokenSource.Default, saved.TokenSource);
      Assert.Equal(ForgeKind.Gitea, saved.Forge);
      Assert.Null(_vault.TryRetrieve(saved.TokenEntryId));
      Assert.Throws<ArgumentException>(() => NewStore().Save(account, "a token it has no place for")); // as for any account on a default token
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
      string before = ForgesFileText;

      ArgumentException ex = Assert.Throws<ArgumentException>(
          () => store.Save(account with { Server = "https://elsewhere.example.test" }, token: null));

      Assert.Contains("is on git.example.test, and that cannot be changed: its token was saved for that place", ex.Message, StringComparison.Ordinal);
      Assert.Contains("Delete the account and add it again", ex.Message, StringComparison.Ordinal);
      Assert.Equal(before, ForgesFileText);
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
      // The wizard of an account is given the default token of the account's place: this server has none.
      var wizard = new AccountWizardViewModel(
          store, new FakeOrganizationLister(), new AppSettings(), account, () => "gitea_token", defaultToken: () => null);

      // GitHub's default token is not on offer for an account on a Gitea server.
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
   public async Task TheWizard_ForAGiteaAccountOnTheDefaultToken_WhoseServerHasNone_SendsNothing_AndOffersATokenOfItsOwn()
   {
      _vault.Store(AppSettings.DefaultTokenVaultId, "ghp_default"); // GitHub's: not this account's
      Account account = GiteaAccount() with { TokenSource = TokenSource.Default };
      var lister = new FakeOrganizationLister();

      var wizard = new AccountWizardViewModel(
          NewStore(), lister, new AppSettings(), account, () => null, defaultToken: () => null);

      // It says it uses the default token, and there is none for its server: the choice is shown, so it can be given a token of its own.
      Assert.True(wizard.UseDefaultToken);
      Assert.True(wizard.OffersTokenChoice);
      Assert.False(wizard.HasDefaultToken);

      Assert.True(await wizard.TryAdvanceAsync());
      Assert.False(await wizard.TryAdvanceAsync());

      Assert.Equal(
          "The default token is no longer saved. Save one in Settings, or use a different token for this account.",
          wizard.TokenError);
      Assert.Equal(0, lister.Calls);
   }
}
