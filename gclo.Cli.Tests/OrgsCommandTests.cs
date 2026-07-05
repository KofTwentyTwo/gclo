namespace gclo.Cli.Tests;

/// <summary>Covers 'gclo orgs': parsing, plain and JSON output, and error translation.</summary>
public sealed class OrgsCommandTests
{
    private const string EnvVar = "GCLO_ORGS_TEST_TOKEN";

    private static Task<int> Run(FakeOrgLister lister, params string[] args)
        => OrgsCommand.RunAsync(args, lister, new NullLog(), CancellationToken.None);

    private static IDisposable Token(string value)
    {
        Environment.SetEnvironmentVariable(EnvVar, value);
        return new TokenScope();
    }

    private sealed class TokenScope : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable(EnvVar, null);
    }

    [Fact]
    public async Task Help_PrintsUsage_AndReturnsZero()
    {
        using var console = new ConsoleCapture();

        int code = await Run(new FakeOrgLister(), "--help");

        Assert.Equal(0, code);
        Assert.Contains("Usage: gclo orgs", console.Out);
    }

    [Fact]
    public async Task Plain_ListsOneLoginPerLine()
    {
        using var console = new ConsoleCapture();
        using var _ = Token("ghp_x");
        var lister = new FakeOrgLister { Result = ["me", "acme"] };

        int code = await Run(lister, "--token-env", EnvVar);

        Assert.Equal(0, code);
        string[] lines = console.Out.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["me", "acme"], lines);
    }

    [Fact]
    public async Task Json_PrintsAnArray()
    {
        using var console = new ConsoleCapture();
        using var _ = Token("ghp_x");
        var lister = new FakeOrgLister { Result = ["me", "acme"] };

        int code = await Run(lister, "--json", "--token-env", EnvVar);

        Assert.Equal(0, code);
        Assert.Contains("[\"me\",\"acme\"]", console.Out);
    }

    [Fact]
    public async Task UnknownOption_Throws()
    {
        await Assert.ThrowsAsync<CliUsageException>(() => Run(new FakeOrgLister(), "--nope"));
    }

    [Fact]
    public async Task MissingToken_Throws()
    {
        string? original = Environment.GetEnvironmentVariable(TokenOptions.DefaultVariable);
        Environment.SetEnvironmentVariable(TokenOptions.DefaultVariable, null);
        try
        {
            await Assert.ThrowsAsync<CliErrorException>(() => Run(new FakeOrgLister()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenOptions.DefaultVariable, original);
        }
    }

    [Fact]
    public async Task ListerInvalidOperation_BecomesCliError()
    {
        using var _ = Token("ghp_x");
        var lister = new FakeOrgLister { Throw = new InvalidOperationException("bad token (401)") };

        var ex = await Assert.ThrowsAsync<CliErrorException>(() => Run(lister, "--token-env", EnvVar));
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task Canceled_Propagates()
    {
        using var _ = Token("ghp_x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var lister = new FakeOrgLister { Result = ["me"] };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => OrgsCommand.RunAsync(["--token-env", EnvVar], lister, new NullLog(), cts.Token));
    }
}
