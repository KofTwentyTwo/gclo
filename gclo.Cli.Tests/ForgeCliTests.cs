/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using gclo.Engine;
using gclo.ViewModels;


namespace gclo.Cli.Tests;


/// <summary>
/// The command line and forges: which place a run names, which token goes there, and
/// what an account may and may not be changed to. The rules that matter most are
/// about tokens: the one in the environment for one forge is never used for another,
/// and an account's token goes only where the account was added for.
/// </summary>
public sealed class ForgeCliTests : IDisposable
{
   private const string Server = "https://git.example.test/";

   private readonly string _dir =
       Path.Combine(Path.GetTempPath(), "gclo-cli-tests", Guid.NewGuid().ToString("N"));

   private readonly InMemoryVault _vault = new();

   private readonly string? _gitHubBefore = Environment.GetEnvironmentVariable(TokenOptions.DefaultVariable);

   private readonly string? _giteaBefore = Environment.GetEnvironmentVariable(TokenOptions.GiteaVariable);

   private readonly List<ForgeEndpoint> _asked = [];

   private readonly FakeRepoLister _repos = new() { Result = [new RepoDescriptor("tool", "https://x/tool.git", "main", false)] };

   private readonly FakeOrgLister _orgs = new() { Result = ["me", "acme"] };

   private AccountsStore? _store;



   public ForgeCliTests() => Tokens(gitHub: "github-token", gitea: "gitea-token");



   public void Dispose()
   {
      Environment.SetEnvironmentVariable(TokenOptions.DefaultVariable, _gitHubBefore);
      Environment.SetEnvironmentVariable(TokenOptions.GiteaVariable, _giteaBefore);
      try
      {
         Directory.Delete(_dir, recursive: true);
      }
      catch(IOException)
      {
      }
   }



   private static void Tokens(string? gitHub, string? gitea)
   {
      Environment.SetEnvironmentVariable(TokenOptions.DefaultVariable, gitHub);
      Environment.SetEnvironmentVariable(TokenOptions.GiteaVariable, gitea);
   }



   private AccountsStore Store => _store ??= new AccountsStore(_vault, _dir, new NullLog());



   private Func<IActivityLog, (AccountsStore, ITokenVault)> Open => _ => (Store, _vault);



   private IRepositoryLister ReposOf(ForgeEndpoint endpoint)
   {
      _asked.Add(endpoint);
      return _repos;
   }



   private IOrganizationLister OrgsOf(ForgeEndpoint endpoint)
   {
      _asked.Add(endpoint);
      return _orgs;
   }



   private Task<int> Repos(params string[] args) => ReposCommand.RunAsync(args, ReposOf, new NullLog(), CancellationToken.None);



   private Task<int> Orgs(params string[] args) => OrgsCommand.RunAsync(args, OrgsOf, new NullLog(), CancellationToken.None);



   private Task<int> Sync(params string[] args)
       => SyncCommand.RunAsync(args, ReposOf, new FakeGit(), Open, new NullLog(), CancellationToken.None);



   private int Accounts(params string[] args) => AccountsCommand.Run(args, Open, new NullLog());



   private Account AddGiteaAccount(string name = "forge")
   {
      using var console = new ConsoleCapture();
      Assert.Equal(0, Accounts("add", "--name", name, "--org", "acme", "--target", Path.Combine(_dir, "target"), "--server", "git.example.test"));
      return Store.FindByName(name)!;
   }



   // ---------------------------------------------------------------- which place, which token

   [Theory]
   [InlineData("--forge", "gitea", "--server", "git.example.test")]
   [InlineData("--server", Server)] // a server alone means a Gitea server
   [InlineData("--forge", "GITEA", "--server", "https://GIT.example.test")]
   public async Task AGiteaRun_AsksThatServer_WithTheGiteaToken_NeverTheGitHubOne(params string[] forge)
   {
      using var console = new ConsoleCapture();

      int code = await Repos(["--org", "acme", .. forge]);

      Assert.Equal(0, code);
      ForgeEndpoint asked = Assert.Single(_asked);
      Assert.Equal(ForgeKind.Gitea, asked.Kind);
      Assert.Equal(Server, asked.Server!.AbsoluteUri);
      Assert.Equal("gitea-token", _repos.LastToken);
   }



   [Theory]
   [InlineData]
   [InlineData("--forge", "github")]
   [InlineData("--forge", "GitHub")]
   public async Task AGitHubRun_AsksGitHub_WithTheGitHubToken_NeverTheGiteaOne(params string[] forge)
   {
      using var console = new ConsoleCapture();

      int code = await Repos(["--org", "acme", .. forge]);

      Assert.Equal(0, code);
      Assert.Same(ForgeEndpoint.GitHub, Assert.Single(_asked));
      Assert.Equal("github-token", _repos.LastToken);
   }



   [Fact]
   public async Task WithNoGiteaTokenInTheEnvironment_AGiteaRunFails_AndDoesNotFallBackToTheGitHubToken()
   {
      Tokens(gitHub: "github-token", gitea: null);

      CliErrorException repos = await Assert.ThrowsAsync<CliErrorException>(() => Repos("--org", "acme", "--server", Server));
      CliErrorException orgs = await Assert.ThrowsAsync<CliErrorException>(() => Orgs("--server", Server));
      CliErrorException sync = await Assert.ThrowsAsync<CliErrorException>(
          () => Sync("--org", "acme", "--target", _dir, "--server", Server, "--dry-run"));

      Assert.All([repos, orgs, sync], error => Assert.Contains("'GITEA_TOKEN' is not set", error.Message, StringComparison.Ordinal));
      Assert.Null(_repos.LastToken);
   }



   [Fact]
   public async Task WithNoGitHubTokenInTheEnvironment_AGitHubRunFails_AndDoesNotFallBackToTheGiteaToken()
   {
      Tokens(gitHub: null, gitea: "gitea-token");

      CliErrorException error = await Assert.ThrowsAsync<CliErrorException>(() => Repos("--org", "acme"));

      Assert.Contains("'GITHUB_TOKEN' is not set", error.Message, StringComparison.Ordinal);
      Assert.Null(_repos.LastToken);
   }



   [Theory]
   [InlineData("--forge takes github or gitea. (The address of a server goes after --server.)", "--forge", "gitlab")]
   [InlineData(
       "--forge takes github or gitea. (The address of a server goes after --server.)",
       "--forge", "https://someone:s3cret-token@git.example.test")] // an address after the wrong option: not repeated either
   [InlineData("A Gitea account needs the address of its server.", "--forge", "gitea")]
   [InlineData("GitHub has no server address to choose: it is always github.com.", "--forge", "github", "--server", Server)]
   [InlineData("The server address cannot be used: it has to begin with https://.", "--server", "http://git.example.test")]
   [InlineData(
       "The server address cannot be used: it contains a user name or a password; the token is entered separately.",
       "--server", "https://someone:s3cret-token@git.example.test")] // not repeated: the secret is in it
   public async Task WhatNamesNoPlace_IsAUsageError_BeforeAnythingIsAsked(string said, params string[] forge)
   {
      CliUsageException repos = await Assert.ThrowsAsync<CliUsageException>(() => Repos(["--org", "acme", .. forge]));
      CliUsageException orgs = await Assert.ThrowsAsync<CliUsageException>(() => Orgs(forge));
      CliUsageException sync = await Assert.ThrowsAsync<CliUsageException>(() => Sync(["--org", "acme", "--target", _dir, .. forge]));
      CliUsageException add = Assert.Throws<CliUsageException>(
          () => Accounts(["add", "--name", "n", "--org", "acme", "--target", _dir, .. forge]));

      Assert.All([repos, orgs, sync, add], error => Assert.Equal(said, error.Message));
      Assert.Empty(_asked);
   }



   [Fact]
   public async Task Orgs_OfAGiteaServer_AreAskedThere_WithTheGiteaToken()
   {
      using var console = new ConsoleCapture();

      int code = await Orgs("--server", Server);

      Assert.Equal(0, code);
      Assert.Equal(ForgeKind.Gitea, Assert.Single(_asked).Kind);
      Assert.Contains("acme", console.Out, StringComparison.Ordinal);
   }



   [Fact]
   public async Task Sync_OfAGiteaServer_ListsThere()
   {
      using var console = new ConsoleCapture();

      int code = await Sync("--org", "acme", "--target", Path.Combine(_dir, "t"), "--forge", "gitea", "--server", Server, "--dry-run");

      Assert.Equal(0, code);
      Assert.Equal(Server, Assert.Single(_asked).Server!.AbsoluteUri);
      Assert.Equal("gitea-token", _repos.LastToken);
      Assert.Contains("tool", console.Out, StringComparison.Ordinal);
   }



   [Theory]
   [InlineData(ForgeAccessKind.Unauthorized, ExitCodes.Auth)]
   [InlineData(ForgeAccessKind.Forbidden, ExitCodes.Auth)]
   [InlineData(ForgeAccessKind.RateLimited, ExitCodes.Transient)]
   [InlineData(ForgeAccessKind.Unavailable, ExitCodes.Transient)]
   [InlineData(ForgeAccessKind.NotFound, ExitCodes.Fatal)]
   public async Task AFailureOfAnyForge_HasTheExitCodeOfItsKind(ForgeAccessKind kind, int exitCode)
   {
      _repos.Throw = new ForgeAccessException(kind, "what the server said");

      CliErrorException error = await Assert.ThrowsAsync<CliErrorException>(() => Repos("--org", "acme", "--server", Server));

      Assert.Equal(exitCode, error.ExitCode);
      Assert.Equal(exitCode, ExitCodes.ForAccess(kind));
      Assert.Equal("what the server said", error.Message);
   }



   [Fact]
   public void EachForge_HasItsOwnDefaultTokenVariable()
   {
      Assert.Equal("GITHUB_TOKEN", TokenOptions.DefaultVariableFor(ForgeKind.GitHub));
      Assert.Equal("GITEA_TOKEN", TokenOptions.DefaultVariableFor(ForgeKind.Gitea));
   }



   // ---------------------------------------------------------------- accounts

   [Fact]
   public void AccountsAdd_ForAGiteaServer_SavesTheForgeAndTheNormalizedServer_AndTheGiteaTokenInItsOwnEntry()
   {
      using var console = new ConsoleCapture();

      int code = Accounts("add", "--name", "forge", "--org", "acme", "--target", Path.Combine(_dir, "target"), "--server", "git.example.test");

      Assert.Equal(0, code);
      Assert.Contains("Account 'forge' added on git.example.test.", console.Out, StringComparison.Ordinal);
      Account account = Store.FindByName("forge")!;
      Assert.Equal(ForgeKind.Gitea, account.Forge);
      Assert.Equal(Server, account.Server);
      Assert.Equal("gitea-token", _vault.TryRetrieve(account.TokenEntryId));
      Assert.Null(_vault.TryRetrieve(account.Id)); // where a gclo that knows only GitHub would look
   }



   [Fact]
   public void AccountsAdd_ForGitHub_IsAsBefore()
   {
      using var console = new ConsoleCapture();

      int code = Accounts("add", "--name", "work", "--org", "acme", "--target", Path.Combine(_dir, "target"));

      Assert.Equal(0, code);
      Assert.Contains("Account 'work' added.", console.Out, StringComparison.Ordinal);
      Account account = Store.FindByName("work")!;
      Assert.Equal(ForgeKind.GitHub, account.Forge);
      Assert.Null(account.Server);
      Assert.Equal("github-token", _vault.TryRetrieve(account.Id));
   }



   [Fact]
   public void AccountsAdd_OnTheDefaultToken_IsForGitHubOnly()
   {
      _vault.Store(AppSettings.DefaultTokenVaultId, "ghp_default");

      CliUsageException error = Assert.Throws<CliUsageException>(
          () => Accounts("add", "--name", "forge", "--org", "acme", "--target", _dir, "--server", Server, "--use-default-token"));

      Assert.Equal(
          "--use-default-token is for GitHub accounts: the default token is a GitHub token. Give the account on git.example.test a token of its own.",
          error.Message);
      Assert.Empty(Store.GetAll());
   }



   [Fact]
   public void AccountsList_SaysWhereEachAccountLives()
   {
      AddGiteaAccount();
      Store.Save(new Account { Id = Guid.NewGuid(), Name = "work", Organization = "acme", TargetRoot = _dir }, "ghp");
      using var console = new ConsoleCapture();

      Assert.Equal(0, Accounts());
      Assert.Equal(0, Accounts("--json"));

      string[] lines = console.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
      Assert.Contains(lines, line => line.StartsWith("forge", StringComparison.Ordinal) && line.Contains("  git.example.test  ", StringComparison.Ordinal));
      Assert.Contains(lines, line => line.StartsWith("work", StringComparison.Ordinal) && line.Contains("  GitHub  ", StringComparison.Ordinal));
      Assert.Contains("\"forge\":\"gitea\",\"server\":\"https://git.example.test/\"", console.Out, StringComparison.Ordinal);
      Assert.Contains("\"forge\":\"github\",\"server\":null", console.Out, StringComparison.Ordinal);
   }



   [Theory]
   [InlineData("--forge", "github")]
   [InlineData("--server", "https://elsewhere.example.test")]
   public void AccountsEdit_CannotMoveAnAccountToAnotherPlace(string option, string value)
   {
      Account before = AddGiteaAccount();

      CliUsageException error = Assert.Throws<CliUsageException>(() => Accounts("edit", "--name", "forge", option, value));

      Assert.Contains("cannot be changed: its token was saved for that place", error.Message, StringComparison.Ordinal);
      Assert.Equal(before, Store.FindByName("forge"));
   }



   [Fact]
   public void AccountsEdit_AGiteaAccount_KeepsATokenOfItsOwn_AndATokenOptionReplacesIt()
   {
      Account account = AddGiteaAccount();
      _vault.Store(AppSettings.DefaultTokenVaultId, "ghp_default");
      Environment.SetEnvironmentVariable("GCLO_FORGE_TEST_TOKEN", "replaced");
      using var console = new ConsoleCapture();
      try
      {
         CliErrorException error = Assert.Throws<CliErrorException>(() => Accounts("edit", "--name", "forge", "--use-default-token"));
         Assert.Equal(
             "Account 'forge' is on git.example.test: the default token is a GitHub token, so it keeps a token of its own.",
             error.Message);

         Assert.Equal(0, Accounts("edit", "--name", "forge", "--token-env", "GCLO_FORGE_TEST_TOKEN"));
      }
      finally
      {
         Environment.SetEnvironmentVariable("GCLO_FORGE_TEST_TOKEN", null);
      }

      Assert.Equal("replaced", _vault.TryRetrieve(account.TokenEntryId));
      Assert.Null(_vault.TryRetrieve(account.Id));
      Assert.Equal(TokenSource.Own, Store.FindByName("forge")!.TokenSource);
   }



   // ---------------------------------------------------------------- sync --account

   [Fact]
   public async Task SyncOfAnAccount_GoesWhereTheAccountWasAddedFor_WithItsOwnToken()
   {
      AddGiteaAccount();
      Tokens(gitHub: "github-token", gitea: "another-gitea-token"); // neither is the account's
      using var console = new ConsoleCapture();

      int code = await Sync("--account", "forge", "--dry-run");

      Assert.Equal(0, code);
      ForgeEndpoint asked = Assert.Single(_asked);
      Assert.Equal(Server, asked.Server!.AbsoluteUri);
      Assert.Equal("gitea-token", _repos.LastToken); // the one that was saved with the account
   }



   [Theory]
   [InlineData("--forge", "github")]
   [InlineData("--server", "https://elsewhere.example.test")]
   public async Task SyncOfAnAccount_TakesNoOtherPlaceFromTheCommandLine(string option, string value)
   {
      AddGiteaAccount();

      CliUsageException error = await Assert.ThrowsAsync<CliUsageException>(() => Sync("--account", "forge", option, value, "--dry-run"));

      Assert.Equal("--forge and --server cannot be used with --account: the account says where it lives.", error.Message);
      Assert.Empty(_asked);
      Assert.Null(_repos.LastToken);
   }



   [Fact]
   public async Task AnAccountThatCannotBeUsedAsItIsSaved_IsNotSynced_AndIsListedAsSuch()
   {
      // A file edited by hand: the server over plain http.
      Account account = AddGiteaAccount();
      string file = Path.Combine(_dir, "accounts-forges.json");
      File.WriteAllText(file, File.ReadAllText(file).Replace(Server, "http://git.example.test/", StringComparison.Ordinal));
      _store = null; // read the file again
      using var console = new ConsoleCapture();

      CliErrorException error = await Assert.ThrowsAsync<CliErrorException>(() => Sync("--account", "forge", "--dry-run"));
      Assert.Equal(0, Accounts());

      Assert.Equal(
          "Account 'forge' cannot be used as it is saved: the server address cannot be used: "
          + "it has to begin with https://. Remove the account and add it again.",
          error.Message);
      Assert.Empty(_asked);
      Assert.Null(_repos.LastToken);
      Assert.Contains("  gitea (not usable)  ", console.Out, StringComparison.Ordinal);
      Assert.Equal("gitea-token", _vault.TryRetrieve(account.TokenEntryId)); // untouched, and sent nowhere
   }



   [Fact]
   public void TheJsonListing_GivesTheAddressATokenGoesTo_AndNeverOneThatWasRefused()
   {
      // A file edited by hand: a password in the address of one account, a forge of a later version on another.
      AddGiteaAccount("edited");
      AddGiteaAccount("forge");
      AddGiteaAccount("later");
      string file = Path.Combine(_dir, "accounts-forges.json");
      string[] parts = File.ReadAllText(file).Split("\"Server\": \"https://git.example.test/\"");
      Assert.Equal(4, parts.Length);
      File.WriteAllText(
          file,
          parts[0] + "\"Server\": \"https://bot:hunter2@git.example.test/\""
          + parts[1] + "\"Server\": \"https://git.example.test/\""
          + parts[2].Replace("\"Forge\": \"Gitea\"", "\"Forge\": \"GitLab\"", StringComparison.Ordinal) + "\"Server\": \"https://git.example.test/\""
          + parts[3]);
      _store = null; // read the file again
      using var console = new ConsoleCapture();

      Assert.Equal(0, Accounts("--json"));

      using var listed = JsonDocument.Parse(console.Out);
      var byName = listed.RootElement.EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!, StringComparer.Ordinal);
      Assert.DoesNotContain("hunter2", console.Out, StringComparison.Ordinal);
      Assert.Equal("gitea", byName["edited"].GetProperty("forge").GetString());
      Assert.Equal(JsonValueKind.Null, byName["edited"].GetProperty("server").ValueKind);
      Assert.Equal("gitea", byName["forge"].GetProperty("forge").GetString());
      Assert.Equal("https://git.example.test/", byName["forge"].GetProperty("server").GetString());
      Assert.Equal("unknown", byName["later"].GetProperty("forge").GetString());
      Assert.Equal(JsonValueKind.Null, byName["later"].GetProperty("server").ValueKind);
   }



   [Fact]
   public async Task AnAccountOnAForgeThisVersionDoesNotKnow_IsNotSynced_AndIsToldWhichVersionToUse()
   {
      AddGiteaAccount();
      string file = Path.Combine(_dir, "accounts-forges.json");
      File.WriteAllText(file, File.ReadAllText(file).Replace("\"Forge\": \"Gitea\"", "\"Forge\": \"GitLab\"", StringComparison.Ordinal));
      _store = null; // read the file again
      using var console = new ConsoleCapture();

      CliErrorException error = await Assert.ThrowsAsync<CliErrorException>(() => Sync("--account", "forge", "--dry-run"));
      Assert.Equal(0, Accounts());

      Assert.Equal(
          "Account 'forge' cannot be used as it is saved: it is on a forge this version of gclo does not know. "
          + "Use the version of gclo that added it.",
          error.Message);
      Assert.Empty(_asked);
      Assert.Null(_repos.LastToken);
      Assert.Contains("  unknown (not usable)  ", console.Out, StringComparison.Ordinal);
   }



   [Fact]
   public async Task TwoAccountsOfOneName_AreNotChosenBetween()
   {
      // An older gclo does not see the accounts on another forge: it can add a GitHub account with the name of one.
      Account forge = AddGiteaAccount("platform");
      File.WriteAllText(
          Path.Combine(_dir, "accounts.json"),
          $$"""[{ "Id": "{{Guid.NewGuid()}}", "Name": "Platform", "Organization": "octo", "TargetRoot": "C:\\repos" }]""");
      _store = null; // read the files again
      using var console = new ConsoleCapture();
      const string Said = "Two accounts are named 'Platform', so the name does not say which one is meant. "
          + "Give one of them another name in the gclo desktop app (Edit account), then try again.";

      CliErrorException sync = await Assert.ThrowsAsync<CliErrorException>(() => Sync("--account", "platform", "--dry-run"));
      CliErrorException edit = Assert.Throws<CliErrorException>(() => Accounts("edit", "--name", "platform", "--parallel", "2"));
      CliErrorException remove = Assert.Throws<CliErrorException>(() => Accounts("remove", "--name", "platform"));

      Assert.All([sync, edit, remove], error => Assert.Equal(Said, error.Message));
      Assert.All([sync, edit, remove], error => Assert.Equal(ExitCodes.Fatal, error.ExitCode)); // 2, as docs/CLI.md says
      Assert.Empty(_asked);
      Assert.Null(_repos.LastToken);
      Assert.Equal(2, Store.GetAll().Count);
      Assert.Equal("gitea-token", _vault.TryRetrieve(forge.TokenEntryId));
   }
}
