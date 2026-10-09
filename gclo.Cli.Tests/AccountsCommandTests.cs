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
        catch (IOException)
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
        if (lastSync is { } when)
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
        using var _ = TokenEnv("ghp_added");
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
        using var _ = TokenEnv("ghp_x");
        Assert.Throws<CliUsageException>(
            () => AccountsCommand.Run(["add", .. options, "--token-env", EnvVar], Open, new NullLog()));
    }

    [Fact]
    public void Add_DuplicateName_IsAFatalError()
    {
        Seed("work", "acme", @"C:\a");
        using var _ = TokenEnv("ghp_x");

        var ex = Assert.Throws<CliErrorException>(
            () => AccountsCommand.Run(["add", "--name", "WORK", "--org", "other", "--target", @"C:\b", "--token-env", EnvVar], Open, new NullLog()));

        Assert.Contains("already exists", ex.Message);
        Assert.Single(Store.GetAll());
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
        using var _ = TokenEnv("ghp_new");
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
        var ex = Assert.Throws<CliUsageException>(() => AccountsCommand.Run(["edit", "--name", "work"], Open, new NullLog()));
        Assert.Contains("Nothing to change", ex.Message);
    }

    [Fact]
    public void Edit_MissingName_IsAUsageError()
        => Assert.Throws<CliUsageException>(() => AccountsCommand.Run(["edit", "--org", "x"], Open, new NullLog()));

    [Fact]
    public void Edit_UnknownAccount_ListsTheOthers()
    {
        Seed("work", "acme", @"C:\a");
        var ex = Assert.Throws<CliErrorException>(
            () => AccountsCommand.Run(["edit", "--name", "ghost", "--org", "x"], Open, new NullLog()));
        Assert.Contains("No account named 'ghost'", ex.Message);
        Assert.Contains("Available accounts: work", ex.Message);
    }

    [Fact]
    public void Edit_RenameOntoAnotherAccount_IsAFatalError()
    {
        Seed("work", "acme", @"C:\a");
        Seed("home", "me", @"C:\h");
        var ex = Assert.Throws<CliErrorException>(
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
        var ex = Assert.Throws<CliErrorException>(() => AccountsCommand.Run(["remove", "--name", "ghost"], Open, new NullLog()));
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
        using var _ = TokenEnv("ghp_x");
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
}
