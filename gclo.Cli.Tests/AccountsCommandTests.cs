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
    }

    [Fact]
    public void OpenThrows_Propagates()
    {
        Func<IActivityLog, (AccountsStore, ITokenVault)> failing =
            _ => throw new CliErrorException("Accounts require Windows credential storage; ...");

        Assert.Throws<CliErrorException>(() => AccountsCommand.Run([], failing, new NullLog()));
    }
}
