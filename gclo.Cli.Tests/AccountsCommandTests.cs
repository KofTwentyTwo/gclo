/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.Engine;
using gclo.ViewModels;


namespace gclo.Cli.Tests;


/// <summary>Covers 'gclo accounts': parsing, empty/populated listings, and JSON output.</summary>
public sealed class AccountsCommandTests : IDisposable
{
   private readonly string _dir =
       Path.Combine(Path.GetTempPath(), "gclo-cli-tests", Guid.NewGuid().ToString("N"));

   private readonly InMemoryVault _vault = new();

   private AccountsStore? _store;



   public void Dispose()
   {
      try
      {
         Directory.Delete(_dir, recursive: true);
      }
      catch(IOException)
      {
      }
   }



   private AccountsStore Store => _store ??= new AccountsStore(_vault, _dir, new NullLog());



   private Func<IActivityLog, (AccountsStore, ITokenVault)> Open => _ => (Store, _vault);



   private void Seed(string name, string org, string target, DateTimeOffset? lastSync = null)
   {
      Store.Save(
          new Account { Id = Guid.NewGuid(), Name = name, Organization = org, TargetRoot = target },
          "tok-" + name);
      if(lastSync is { } when)
      {
         Account saved = Store.FindByName(name)!;
         Store.RecordSyncResult(saved.Id, when, "Finished.");
      }
   }



   [Fact]
   public void Help_PrintsUsage()
   {
      using var console = new ConsoleCapture();

      int code = AccountsCommand.Run(["--help"], Open, new NullLog());

      Assert.Equal(0, code);
      Assert.Contains("Usage: gclo accounts", console.Out);
   }



   [Fact]
   public void UnknownOption_Throws()
       => Assert.Throws<CliUsageException>(() => AccountsCommand.Run(["--nope"], Open, new NullLog()));



   [Fact]
   public void NoAccounts_WritesHintToStderr_AndReturnsZero()
   {
      using var console = new ConsoleCapture();

      int code = AccountsCommand.Run([], Open, new NullLog());

      Assert.Equal(0, code);
      Assert.Equal("", console.Out); // stdout stays clean
      Assert.Contains("No accounts yet", console.Error);
   }



   [Fact]
   public void Populated_PrintsAlignedColumns_WithNeverAndTimestamp()
   {
      Seed("kof", "KofTwentyTwo", @"R:\repos\kof");
      Seed("work", "acme", @"C:\work", new DateTimeOffset(2026, 7, 4, 15, 30, 0, TimeSpan.Zero));
      using var console = new ConsoleCapture();

      int code = AccountsCommand.Run([], Open, new NullLog());

      Assert.Equal(0, code);
      Assert.Contains("kof", console.Out);
      Assert.Contains("never", console.Out); // kof never synced
      Assert.Contains("2026-07-04", console.Out); // work's last sync (local date)
   }



   [Fact]
   public void Json_PrintsSummaries()
   {
      Seed("kof", "KofTwentyTwo", @"R:\repos\kof");
      using var console = new ConsoleCapture();

      int code = AccountsCommand.Run(["--json"], Open, new NullLog());

      Assert.Equal(0, code);
      Assert.Contains("\"name\":\"kof\"", console.Out);
      Assert.Contains("\"organization\":\"KofTwentyTwo\"", console.Out);
      Assert.Contains("\"lastSync\":null", console.Out);
      // The fields a script needs to reason about a run.
      Assert.Contains("\"id\":\"" + Store.FindByName("kof")!.Id.ToString("N") + "\"", console.Out);
      Assert.Contains("\"createOrgSubfolder\":false", console.Out);
      Assert.Contains("\"maxConcurrency\":8", console.Out);
      Assert.Contains("\"lastSyncSummary\":null", console.Out);
   }



   [Fact]
   public void ExplicitListSubcommand_IsTheDefault()
   {
      Seed("kof", "KofTwentyTwo", @"R:\repos\kof");
      using var console = new ConsoleCapture();

      Assert.Equal(0, AccountsCommand.Run(["list"], Open, new NullLog()));
      Assert.Contains("kof", console.Out);
   }



   [Fact]
   public void UnknownSubcommand_Throws()
       => Assert.Throws<CliUsageException>(() => AccountsCommand.Run(["frobnicate"], Open, new NullLog()));



   // ---------------------------------------------------------------- add

   private const string EnvVar = "GCLO_ACCOUNTS_TEST_TOKEN";



   private static IDisposable TokenEnv(string value)
   {
      Environment.SetEnvironmentVariable(EnvVar, value);
      return new EnvScope();
   }



   private sealed class EnvScope : IDisposable
   {
      public void Dispose() => Environment.SetEnvironmentVariable(EnvVar, null);
   }



   [Fact]
   public void Add_CreatesTheAccount_AndStoresTheToken()
   {
      using IDisposable _ = TokenEnv("ghp_added");
      using var console = new ConsoleCapture();

      int code = AccountsCommand.Run(
          ["add", "--name", "work", "--org", "acme", "--target", @"C:\src", "--parallel", "12",
             "--org-subfolder", "--description", "day job", "--token-env", EnvVar],
          Open, new NullLog());

      Assert.Equal(0, code);
      Assert.Contains("Account 'work' added.", console.Out);
      Account saved = Assert.Single(Store.GetAll());
      Assert.Equal("acme", saved.Organization);
      Assert.Equal(@"C:\src", saved.TargetRoot);
      Assert.Equal(12, saved.MaxConcurrency);
      Assert.True(saved.CreateOrgSubfolder);
      Assert.Equal("day job", saved.Description);
      Assert.Equal("ghp_added", _vault.TryRetrieve(saved.Id));
   }



   [Fact]
   public void Add_ReadsTheTokenFromStdin()
   {
      using var console = new ConsoleCapture();
      using var stdin = new StdinRedirect("ghp_piped\n");

      int code = AccountsCommand.Run(
          ["add", "--name", "work", "--org", "acme", "--target", @"C:\src", "--token-stdin"], Open, new NullLog());

      Assert.Equal(0, code);
      Assert.Equal("ghp_piped", _vault.TryRetrieve(Store.FindByName("work")!.Id));
   }



   [Theory]
   [InlineData("--org", "acme", "--target", @"C:\src")]
   [InlineData("--name", "work", "--target", @"C:\src")]
   [InlineData("--name", "work", "--org", "acme")]
   public void Add_MissingRequiredOption_IsAUsageError(params string[] options)
   {
      using IDisposable _ = TokenEnv("ghp_x");
      Assert.Throws<CliUsageException>(
          () => AccountsCommand.Run(["add", .. options, "--token-env", EnvVar], Open, new NullLog()));
   }



   [Fact]
   public void Add_DuplicateName_IsAFatalError()
   {
      Seed("work", "acme", @"C:\a");
      using IDisposable _ = TokenEnv("ghp_x");

      CliErrorException ex = Assert.Throws<CliErrorException>(
          () => AccountsCommand.Run(["add", "--name", "WORK", "--org", "other", "--target", @"C:\b", "--token-env", EnvVar], Open, new NullLog()));

      Assert.Contains("already exists", ex.Message);
      Assert.Single(Store.GetAll());
   }



   // ---------------------------------------------------------------- default token (#102)

   [Fact]
   public void Add_UseDefaultToken_CreatesAnAccountWithoutAnOwnEntry()
   {
      _vault.Store(AppSettings.DefaultTokenVaultId, "ghp_default");
      using var console = new ConsoleCapture();

      int code = AccountsCommand.Run(
          ["add", "--name", "shared", "--org", "acme", "--target", @"C:\src", "--use-default-token"], Open, new NullLog());

      Assert.Equal(0, code);
      Account saved = Store.FindByName("shared")!;
      Assert.True(saved.UsesDefaultToken);
      Assert.Null(_vault.TryRetrieve(saved.Id));
      Assert.Contains("uses the default token", console.Out);
   }



   [Fact]
   public void Add_UseDefaultToken_WithoutASavedDefault_IsAFatalError()
   {
      CliErrorException ex = Assert.Throws<CliErrorException>(() => AccountsCommand.Run(
          ["add", "--name", "shared", "--org", "acme", "--target", @"C:\src", "--use-default-token"], Open, new NullLog()));

      Assert.Contains("No default token is saved", ex.Message);
      Assert.Contains("Settings", ex.Message);
      Assert.Empty(Store.GetAll());
   }



   [Fact]
   public void Add_UseDefaultToken_WithAnotherTokenOption_IsAUsageError()
   {
      _vault.Store(AppSettings.DefaultTokenVaultId, "ghp_default");
      using IDisposable _ = TokenEnv("ghp_x");

      Assert.Throws<CliUsageException>(() => AccountsCommand.Run(
          ["add", "--name", "shared", "--org", "acme", "--target", @"C:\src", "--use-default-token", "--token-env", EnvVar],
          Open, new NullLog()));
      Assert.Empty(Store.GetAll());
   }



   [Fact]
   public void Edit_UseDefaultToken_SwitchesTheAccount_AndRemovesItsOwnToken()
   {
      Seed("work", "acme", @"C:\a");
      _vault.Store(AppSettings.DefaultTokenVaultId, "ghp_default");
      Guid id = Store.FindByName("work")!.Id;
      Assert.Equal("tok-work", _vault.TryRetrieve(id));
      using var console = new ConsoleCapture();

      int code = AccountsCommand.Run(["edit", "--name", "work", "--use-default-token"], Open, new NullLog());

      Assert.Equal(0, code);
      Assert.True(Store.FindByName("work")!.UsesDefaultToken);
      Assert.Null(_vault.TryRetrieve(id));
      Assert.Contains("now uses the default token", console.Out);
   }



   [Fact]
   public void Edit_UseDefaultToken_WithoutASavedDefault_IsAFatalError_AndKeepsTheOwnToken()
   {
      Seed("work", "acme", @"C:\a");
      Guid id = Store.FindByName("work")!.Id;

      Assert.Throws<CliErrorException>(() => AccountsCommand.Run(["edit", "--name", "work", "--use-default-token"], Open, new NullLog()));

      Assert.False(Store.FindByName("work")!.UsesDefaultToken);
      Assert.Equal("tok-work", _vault.TryRetrieve(id));
   }



   [Fact]
   public void Edit_TokenOption_SwitchesADefaultTokenAccountToItsOwnToken()
   {
      _vault.Store(AppSettings.DefaultTokenVaultId, "ghp_default");
      AccountsCommand.Run(["add", "--name", "shared", "--org", "acme", "--target", @"C:\src", "--use-default-token"], Open, new NullLog());
      using IDisposable _ = TokenEnv("ghp_mine");

      int code = AccountsCommand.Run(["edit", "--name", "shared", "--token-env", EnvVar], Open, new NullLog());

      Assert.Equal(0, code);
      Account updated = Store.FindByName("shared")!;
      Assert.False(updated.UsesDefaultToken);
      Assert.Equal("ghp_mine", _vault.TryRetrieve(updated.Id));
   }



   [Fact]
   public void Edit_OtherSettings_LeaveTheTokenSourceAlone()
   {
      _vault.Store(AppSettings.DefaultTokenVaultId, "ghp_default");
      AccountsCommand.Run(["add", "--name", "shared", "--org", "acme", "--target", @"C:\src", "--use-default-token"], Open, new NullLog());

      AccountsCommand.Run(["edit", "--name", "shared", "--parallel", "3"], Open, new NullLog());

      Account updated = Store.FindByName("shared")!;
      Assert.True(updated.UsesDefaultToken);
      Assert.Equal(3, updated.MaxConcurrency);
   }



   [Fact]
   public void List_ShowsTheTokenSource_InColumnsAndJson()
   {
      Seed("work", "acme", @"C:\a");
      _vault.Store(AppSettings.DefaultTokenVaultId, "ghp_default");
      AccountsCommand.Run(["add", "--name", "shared", "--org", "acme", "--target", @"C:\b", "--use-default-token"], Open, new NullLog());

      using(var console = new ConsoleCapture())
      {
         Assert.Equal(0, AccountsCommand.Run([], Open, new NullLog()));
         string[] lines = console.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
         Assert.Contains(lines, l => l.StartsWith("shared", StringComparison.Ordinal) && l.Contains("  default  ", StringComparison.Ordinal));
         Assert.Contains(lines, l => l.StartsWith("work", StringComparison.Ordinal) && l.Contains("  own  ", StringComparison.Ordinal));
      }

      using(var console = new ConsoleCapture())
      {
         Assert.Equal(0, AccountsCommand.Run(["--json"], Open, new NullLog()));
         Assert.Contains("\"name\":\"shared\"", console.Out);
         Assert.Contains("\"tokenSource\":\"default\"", console.Out);
         Assert.Contains("\"tokenSource\":\"own\"", console.Out);
      }
   }



   [Fact]
   public void Add_UnknownOption_Throws()
       => Assert.Throws<CliUsageException>(() => AccountsCommand.Run(["add", "--nope"], Open, new NullLog()));



   [Fact]
   public void Add_Help_PrintsUsage()
   {
      using var console = new ConsoleCapture();
      Assert.Equal(0, AccountsCommand.Run(["add", "--help"], Open, new NullLog()));
      Assert.Contains("gclo accounts add", console.Out);
   }



   // ---------------------------------------------------------------- edit

   [Fact]
   public void Edit_ReplacesOnlyTheGivenSettings_AndLeavesTheTokenAlone()
   {
      Seed("work", "acme", @"C:\a");
      Account before = Store.FindByName("work")!;
      using var console = new ConsoleCapture();

      int code = AccountsCommand.Run(
          ["edit", "--name", "work", "--org", "acme-labs", "--parallel", "3", "--org-subfolder", "--description", "moved"],
          Open, new NullLog());

      Assert.Equal(0, code);
      Assert.Contains("Account 'work' updated.", console.Out);
      Account after = Store.FindByName("work")!;
      Assert.Equal(before.Id, after.Id);
      Assert.Equal("acme-labs", after.Organization);
      Assert.Equal(@"C:\a", after.TargetRoot); // untouched
      Assert.Equal(3, after.MaxConcurrency);
      Assert.True(after.CreateOrgSubfolder);
      Assert.Equal("moved", after.Description);
      Assert.Equal("tok-work", _vault.TryRetrieve(after.Id)); // untouched
   }



   [Fact]
   public void Edit_RenameTargetAndNoOrgSubfolder_AndTokenReplacement()
   {
      Seed("work", "acme", @"C:\a");
      Store.Save(Store.FindByName("work")! with { CreateOrgSubfolder = true }, null);
      using IDisposable _ = TokenEnv("ghp_new");
      using var console = new ConsoleCapture();

      int code = AccountsCommand.Run(
          ["edit", "--name", "work", "--rename", "work2", "--target", @"D:\b", "--no-org-subfolder", "--token-env", EnvVar],
          Open, new NullLog());

      Assert.Equal(0, code);
      Assert.Contains("Account 'work2' updated (token replaced).", console.Out);
      Assert.Null(Store.FindByName("work"));
      Account after = Store.FindByName("work2")!;
      Assert.Equal(@"D:\b", after.TargetRoot);
      Assert.False(after.CreateOrgSubfolder);
      Assert.Equal("ghp_new", _vault.TryRetrieve(after.Id));
   }



   [Fact]
   public void Edit_WithNothingToChange_IsAUsageError()
   {
      Seed("work", "acme", @"C:\a");
      CliUsageException ex = Assert.Throws<CliUsageException>(() => AccountsCommand.Run(["edit", "--name", "work"], Open, new NullLog()));
      Assert.Contains("Nothing to change", ex.Message);
   }



   [Fact]
   public void Edit_MissingName_IsAUsageError()
       => Assert.Throws<CliUsageException>(() => AccountsCommand.Run(["edit", "--org", "x"], Open, new NullLog()));



   [Fact]
   public void Edit_UnknownAccount_ListsTheOthers()
   {
      Seed("work", "acme", @"C:\a");
      CliErrorException ex = Assert.Throws<CliErrorException>(
          () => AccountsCommand.Run(["edit", "--name", "ghost", "--org", "x"], Open, new NullLog()));
      Assert.Contains("No account named 'ghost'", ex.Message);
      Assert.Contains("Available accounts: work", ex.Message);
   }



   [Fact]
   public void Edit_RenameOntoAnotherAccount_IsAFatalError()
   {
      Seed("work", "acme", @"C:\a");
      Seed("home", "me", @"C:\h");
      CliErrorException ex = Assert.Throws<CliErrorException>(
          () => AccountsCommand.Run(["edit", "--name", "home", "--rename", "work"], Open, new NullLog()));
      Assert.Contains("already exists", ex.Message);
   }



   [Fact]
   public void Edit_UnknownOption_Throws()
       => Assert.Throws<CliUsageException>(() => AccountsCommand.Run(["edit", "--name", "work", "--nope"], Open, new NullLog()));



   [Fact]
   public void Edit_Help_PrintsUsage()
   {
      using var console = new ConsoleCapture();
      Assert.Equal(0, AccountsCommand.Run(["edit", "--help"], Open, new NullLog()));
      Assert.Contains("gclo accounts edit", console.Out);
   }



   // ---------------------------------------------------------------- remove

   [Fact]
   public void Remove_DeletesTheAccountAndItsToken()
   {
      Seed("work", "acme", @"C:\a");
      Guid id = Store.FindByName("work")!.Id;
      using var console = new ConsoleCapture();

      int code = AccountsCommand.Run(["remove", "--name", "work"], Open, new NullLog());

      Assert.Equal(0, code);
      Assert.Contains("Account 'work' removed.", console.Out);
      Assert.Empty(Store.GetAll());
      Assert.Null(_vault.TryRetrieve(id));
   }



   [Fact]
   public void Remove_UnknownAccount_WhenNoneExist_SaysSo()
   {
      CliErrorException ex = Assert.Throws<CliErrorException>(() => AccountsCommand.Run(["remove", "--name", "ghost"], Open, new NullLog()));
      Assert.Contains("No accounts exist yet", ex.Message);
   }



   [Fact]
   public void Remove_MissingName_IsAUsageError()
       => Assert.Throws<CliUsageException>(() => AccountsCommand.Run(["remove"], Open, new NullLog()));



   [Fact]
   public void Remove_UnknownOption_Throws()
       => Assert.Throws<CliUsageException>(() => AccountsCommand.Run(["remove", "--nope"], Open, new NullLog()));



   [Fact]
   public void Remove_Help_PrintsUsage()
   {
      using var console = new ConsoleCapture();
      Assert.Equal(0, AccountsCommand.Run(["remove", "--help"], Open, new NullLog()));
      Assert.Contains("gclo accounts remove", console.Out);
   }



   [Fact]
   public void Mutations_OpenThrows_Propagates()
   {
      using IDisposable _ = TokenEnv("ghp_x");
      Func<IActivityLog, (AccountsStore, ITokenVault)> failing =
          _ => throw new CliErrorException("Accounts require Windows credential storage; ...");

      Assert.Throws<CliErrorException>(() => AccountsCommand.Run(
          ["add", "--name", "w", "--org", "o", "--target", "t", "--token-env", EnvVar], failing, new NullLog()));
      Assert.Throws<CliErrorException>(() => AccountsCommand.Run(["edit", "--name", "w", "--org", "o"], failing, new NullLog()));
      Assert.Throws<CliErrorException>(() => AccountsCommand.Run(["remove", "--name", "w"], failing, new NullLog()));
   }



   [Fact]
   public void OpenThrows_Propagates()
   {
      Func<IActivityLog, (AccountsStore, ITokenVault)> failing =
          _ => throw new CliErrorException("Accounts require Windows credential storage; ...");

      Assert.Throws<CliErrorException>(() => AccountsCommand.Run([], failing, new NullLog()));
   }



   // ---------------------------------------------------------------- option handling (#159)

   [Fact]
   public void ShortHelp_IsHelp_NotAnUnknownSubcommand()
   {
      using var console = new ConsoleCapture();

      int code = AccountsCommand.Run(["-h"], Open, new NullLog());

      Assert.Equal(0, code);
      Assert.Contains("gclo accounts", console.Out);
   }



   [Fact]
   public void Add_AnEmptyDescription_IsAccepted_InEitherForm()
   {
      using IDisposable _ = TokenEnv("ghp_added");
      using var console = new ConsoleCapture();

      Assert.Equal(0, AccountsCommand.Run(
          ["add", "--name", "one", "--org", "acme", "--target", @"C:\src", "--description=", "--token-env", EnvVar], Open, new NullLog()));
      Assert.Equal(0, AccountsCommand.Run(
          ["add", "--name", "two", "--org", "acme", "--target", @"C:\src", "--description", "", "--token-env", EnvVar], Open, new NullLog()));

      Assert.All(Store.GetAll(), a => Assert.Equal("", a.Description));
   }



   [Fact]
   public void Edit_AnEmptyDescription_ClearsIt()
   {
      Seed("work", "acme", @"C:\src");
      Store.Save(Store.FindByName("work")! with { Description = "day job" }, token: null);
      using var console = new ConsoleCapture();

      int code = AccountsCommand.Run(["edit", "--name", "work", "--description="], Open, new NullLog());

      Assert.Equal(0, code);
      Assert.Equal("", Store.FindByName("work")!.Description);
   }



   [Theory]
   [InlineData("--rename")]
   [InlineData("--org")]
   [InlineData("--target")]
   public void Edit_ABlankNameOrganizationOrFolder_IsAUsageError_AndChangesNothing(string option)
   {
      Seed("work", "acme", @"C:\src");
      Account before = Store.FindByName("work")!;

      CliUsageException ex = Assert.Throws<CliUsageException>(
          () => AccountsCommand.Run(["edit", "--name", "work", option, "  "], Open, new NullLog()));

      Assert.Equal($"{option} expects a value that is not blank.", ex.Message);
      Assert.Equal(before, Store.FindByName("work"));
   }



   [Fact]
   public void ARelativeTarget_IsStoredAsAFullPath_ByAddAndByEdit()
   {
      using IDisposable _ = TokenEnv("ghp_added");
      using var console = new ConsoleCapture();

      AccountsCommand.Run(
          ["add", "--name", "work", "--org", "acme", "--target", "repos", "--token-env", EnvVar], Open, new NullLog());
      string added = Store.FindByName("work")!.TargetRoot;
      AccountsCommand.Run(["edit", "--name", "work", "--target", Path.Combine("other", "place")], Open, new NullLog());
      string edited = Store.FindByName("work")!.TargetRoot;

      // The account will be used from other places; "repos" must mean one folder.
      Assert.Equal(Path.GetFullPath("repos"), added);
      Assert.Equal(Path.GetFullPath(Path.Combine("other", "place")), edited);
      Assert.True(Path.IsPathFullyQualified(added));
   }



   [Theory]
   [InlineData("a|b", "a character no folder name can have")]
   [InlineData("a<b>", "a character no folder name can have")]
   [InlineData("repos*", "a character no folder name can have")]
   [InlineData("~", "are not expanded by gclo")]
   [InlineData(@"~\src", "are not expanded by gclo")]
   [InlineData(@"%USERPROFILE%\src", "are not expanded by gclo")]
   [InlineData("repos:x", "a colon belongs after a drive letter only")]
   [InlineData(@"C:\src:stream", "a colon belongs after a drive letter only")]
   [InlineData("con", "it names a device, not a folder")]
   public void ATargetThatCannotBeAFolder_IsAUsageError_BeforeTheTokenIsEvenRead(string target, string reason)
   {
      if(!OperatingSystem.IsWindows() && string.Equals(target, "con", StringComparison.Ordinal))
      {
         return; // device names are a Windows notion
      }
      using var stdin = new StdinRedirect("ghp_not_to_be_read\n");

      CliUsageException add = Assert.Throws<CliUsageException>(() => AccountsCommand.Run(
          ["add", "--name", "work", "--org", "acme", "--target", target, "--token-stdin"], Open, new NullLog()));
      Seed("existing", "acme", @"C:\src");
      CliUsageException edit = Assert.Throws<CliUsageException>(() => AccountsCommand.Run(
          ["edit", "--name", "existing", "--target", target, "--token-stdin"], Open, new NullLog()));

      Assert.Contains("is not a usable folder path", add.Message);
      Assert.Contains(reason, add.Message);
      Assert.Contains(reason, edit.Message);
      Assert.Null(Store.FindByName("work"));
      Assert.Equal(@"C:\src", Store.FindByName("existing")!.TargetRoot);
      Assert.Equal("ghp_not_to_be_read", Console.In.ReadLine()); // the token line is still there: nothing read it
   }



   [Fact]
   public void ATargetLongerThanAnyPathCanBe_IsAUsageError()
   {
      using IDisposable _ = TokenEnv("ghp_added");
      string tooLong = new('a', 40_000);

      CliUsageException ex = Assert.Throws<CliUsageException>(() => AccountsCommand.Run(
          ["add", "--name", "work", "--org", "acme", "--target", tooLong, "--token-env", EnvVar], Open, new NullLog()));

      Assert.Contains("is not a usable folder path", ex.Message);
      Assert.Empty(Store.GetAll());
   }



   [Fact]
   public void ATargetWithADriveLetter_OrAFullPath_IsKeptAsItIs()
   {
      if(!OperatingSystem.IsWindows())
      {
         return; // drive letters
      }
      using IDisposable _ = TokenEnv("ghp_added");
      using var console = new ConsoleCapture();

      AccountsCommand.Run(["add", "--name", "work", "--org", "acme", "--target", @"D:\mirror\acme", "--token-env", EnvVar], Open, new NullLog());

      Assert.Equal(@"D:\mirror\acme", Store.FindByName("work")!.TargetRoot);
   }



   // ---------------------------------------------------------------- failures of the file or the credential store (#159)

   /// <summary>Makes the next write of accounts.json fail: a folder sits where its temporary file goes.</summary>
   private void BlockAccountsFile() => Directory.CreateDirectory(Path.Combine(_dir, "accounts.json.tmp"));



   [Fact]
   public void AFailureToWriteTheAccountsFile_IsAFatalError_NotAnUnexpectedOne()
   {
      using IDisposable _ = TokenEnv("ghp_added");
      Seed("work", "acme", @"C:\src");
      BlockAccountsFile();

      CliErrorException add = Assert.Throws<CliErrorException>(() => AccountsCommand.Run(
          ["add", "--name", "other", "--org", "acme", "--target", @"C:\src", "--token-env", EnvVar], Open, new NullLog()));
      CliErrorException edit = Assert.Throws<CliErrorException>(
          () => AccountsCommand.Run(["edit", "--name", "work", "--org", "elsewhere"], Open, new NullLog()));
      CliErrorException remove = Assert.Throws<CliErrorException>(
          () => AccountsCommand.Run(["remove", "--name", "work"], Open, new NullLog()));

      Assert.All(new[] { add, edit, remove }, ex =>
      {
         Assert.Equal(ExitCodes.Fatal, ex.ExitCode);
         Assert.IsType<UnauthorizedAccessException>(ex.InnerException);
      });
      Assert.Equal("acme", Store.FindByName("work")!.Organization);
      Assert.Equal("tok-work", _vault.TryRetrieve(Store.FindByName("work")!.Id)); // the removal was undone
   }



   /// <summary>A vault that fails the way Credential Manager does when it cannot be used.</summary>
   private sealed class BrokenVault : ITokenVault
   {
      // What CredentialManagerVault throws: the call that failed and the number, no words for it.
      public void Store(Guid accountId, string token)
          => throw new System.ComponentModel.Win32Exception(1312, "CredWriteW failed (error 1312) for target 'gclo:account:test'.");

      public string? TryRetrieve(Guid accountId) => null;

      public void Delete(Guid accountId)
      {
      }
   }



   [Fact]
   public void AFailureOfTheCredentialStore_IsAFatalError_WithItsReason()
   {
      using IDisposable _ = TokenEnv("ghp_added");
      var vault = new BrokenVault();
      var store = new AccountsStore(vault, _dir, new NullLog());

      CliErrorException ex = Assert.Throws<CliErrorException>(() => AccountsCommand.Run(
          ["add", "--name", "work", "--org", "acme", "--target", @"C:\src", "--token-env", EnvVar], _ => (store, vault), new NullLog()));

      Assert.Equal(ExitCodes.Fatal, ex.ExitCode);
      Assert.StartsWith("CredWriteW failed (error 1312)", ex.Message);
      if(OperatingSystem.IsWindows())
      {
         // The number alone says nothing; what Windows means by it is added.
         Assert.Contains("logon session", ex.Message);
      }
      Assert.Empty(store.GetAll());
   }



   [Fact]
   public void AnEdit_OfAnAccountAnotherGcloProcessRemovedMeanwhile_IsAFatalError_NotAnUnexpectedOne()
   {
      Seed("work", "acme", @"C:\src");
      Account work = Store.FindByName("work")!;
      // The store this command works with still lists the account; the file no longer does.
      new AccountsStore(_vault, _dir, new NullLog()).Delete(work.Id);

      CliErrorException ex = Assert.Throws<CliErrorException>(
          () => AccountsCommand.Run(["edit", "--name", "work", "--org", "elsewhere"], Open, new NullLog()));

      Assert.Equal(ExitCodes.Fatal, ex.ExitCode);
      Assert.Contains("was removed by another gclo process", ex.Message);
   }



   [Fact]
   public void AChange_WhileAnotherGcloProcessHoldsTheAccountsLock_IsRetryLater_AfterTheStoresOwnWait()
   {
      // The real path, once: the store waits its few seconds for the lock, then says so.
      Seed("work", "acme", @"C:\src");
      using var held = new FileStream(Path.Combine(_dir, "accounts.json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

      CliErrorException ex = Assert.Throws<CliErrorException>(
          () => AccountsCommand.Run(["remove", "--name", "work"], Open, new NullLog()));

      Assert.Equal(ExitCodes.Transient, ex.ExitCode);
      Assert.Contains("in use by another gclo process", ex.Message);
      Assert.NotNull(Store.FindByName("work"));
   }



   [Theory]
   [InlineData("list")]
   [InlineData("add")]
   [InlineData("edit")]
   [InlineData("remove")]
   public void AnAccountsFileThatAnotherGcloProcessIsChanging_IsRetryLater(string subcommand)
   {
      using IDisposable _ = TokenEnv("ghp_added");
      Func<IActivityLog, (AccountsStore, ITokenVault)> busy =
          _ => throw new AccountsFileBusyException("The accounts file is in use by another gclo process.", new IOException("sharing violation"));
      string[] args = subcommand switch
      {
         "add" => ["add", "--name", "work", "--org", "acme", "--target", @"C:\src", "--token-env", EnvVar],
         "edit" => ["edit", "--name", "work", "--org", "elsewhere"],
         "remove" => ["remove", "--name", "work"],
         _ => ["list"],
      };

      CliErrorException ex = Assert.Throws<CliErrorException>(() => AccountsCommand.Run(args, busy, new NullLog()));

      Assert.Equal(ExitCodes.Transient, ex.ExitCode);
      Assert.Contains("in use by another gclo process", ex.Message);
   }



   [Fact]
   public void AFailureThatIsNeitherTheFileNorTheStore_StaysWhatItIs()
   {
      Func<IActivityLog, (AccountsStore, ITokenVault)> bug = _ => throw new InvalidOperationException("a bug");

      Assert.Throws<InvalidOperationException>(() => AccountsCommand.Run(["list"], bug, new NullLog()));
   }
}
