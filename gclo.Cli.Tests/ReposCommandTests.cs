using gclo.Engine;

namespace gclo.Cli.Tests;

/// <summary>Covers 'gclo repos': parsing, filters, plain and JSON output, and error translation.</summary>
public sealed class ReposCommandTests
{
    private const string EnvVar = "GCLO_REPOS_TEST_TOKEN";

    private static Task<int> Run(FakeRepoLister lister, params string[] args)
        => ReposCommand.RunAsync(args, lister, new NullLog(), CancellationToken.None);

    private static IDisposable Token(string value)
    {
        Environment.SetEnvironmentVariable(EnvVar, value);
        return new TokenScope();
    }

    private sealed class TokenScope : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable(EnvVar, null);
    }

    private static RepoDescriptor Repo(string name, string? branch = "main", bool archived = false)
        => new(name, $"https://x/{name}.git", branch, archived);

    [Fact]
    public async Task Help_PrintsUsage_AndReturnsZero()
    {
        using var console = new ConsoleCapture();

        int code = await Run(new FakeRepoLister(), "--help");

        Assert.Equal(0, code);
        Assert.Contains("Usage: gclo repos", console.Out);
    }

    [Fact]
    public async Task MissingOrg_IsAUsageError()
        => await Assert.ThrowsAsync<CliUsageException>(() => Run(new FakeRepoLister()));

    [Fact]
    public async Task UnknownOption_Throws()
        => await Assert.ThrowsAsync<CliUsageException>(() => Run(new FakeRepoLister(), "--org", "acme", "--nope"));

    [Fact]
    public async Task Plain_PrintsAlignedColumns_WithBranchAndArchivedMarker()
    {
        using var console = new ConsoleCapture();
        using var _ = Token("ghp_x");
        var lister = new FakeRepoLister { Result = [Repo("api"), Repo("empty-one", null), Repo("old", "master", archived: true)] };

        int code = await Run(lister, "--org", "acme", "--token-env", EnvVar);

        Assert.Equal(0, code);
        string[] lines = console.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd()).ToArray();
        // Name and branch columns are padded to the widest value; 'archived' trails only archived rows.
        Assert.Equal(["api        main", "empty-one  -", "old        master  archived"], lines);
    }

    [Fact]
    public async Task Filters_NarrowTheListing_AndAnEmptyResultSaysWhy()
    {
        using var console = new ConsoleCapture();
        using var _ = Token("ghp_x");
        var lister = new FakeRepoLister { Result = [Repo("platform-api"), Repo("platform-old", archived: true), Repo("docs")] };

        int code = await Run(lister, "--org", "acme", "--token-env", EnvVar, "--include", "platform-*", "--skip-archived");
        Assert.Equal(0, code);
        Assert.Contains("platform-api", console.Out);
        Assert.DoesNotContain("platform-old", console.Out);
        Assert.DoesNotContain("docs", console.Out);

        code = await Run(lister, "--org", "acme", "--token-env", EnvVar, "--include", "nothing-*", "--exclude", "x");
        Assert.Equal(0, code);
        Assert.Contains("No repositories of 'acme' match the filters (3 listed).", console.Error);

        code = await Run(new FakeRepoLister(), "--org", "acme", "--token-env", EnvVar);
        Assert.Equal(0, code);
        Assert.Contains("No repositories visible in 'acme'.", console.Error);
    }

    [Fact]
    public async Task Json_PrintsAnArrayOfSummaries()
    {
        using var console = new ConsoleCapture();
        using var _ = Token("ghp_x");
        var lister = new FakeRepoLister { Result = [Repo("api"), Repo("old", null, archived: true)] };

        int code = await Run(lister, "--org", "acme", "--token-env", EnvVar, "--json");

        Assert.Equal(0, code);
        Assert.Equal(
            "[{\"name\":\"api\",\"defaultBranch\":\"main\",\"archived\":false,\"cloneUrl\":\"https://x/api.git\"},"
            + "{\"name\":\"old\",\"defaultBranch\":null,\"archived\":true,\"cloneUrl\":\"https://x/old.git\"}]",
            console.Out.Trim());
    }

    [Fact]
    public async Task ListerAccessRefusal_MapsToTheExitCodeTaxonomy()
    {
        using var _ = Token("ghp_x");
        var lister = new FakeRepoLister { Throw = new GitHubAccessException(GitHubAccessKind.Forbidden, "no access") };

        var ex = await Assert.ThrowsAsync<CliErrorException>(() => Run(lister, "--org", "acme", "--token-env", EnvVar));

        Assert.Equal(ExitCodes.Auth, ex.ExitCode);
        Assert.Contains("no access", ex.Message);
    }

    [Fact]
    public async Task MissingToken_Throws()
    {
        string? original = Environment.GetEnvironmentVariable(TokenOptions.DefaultVariable);
        Environment.SetEnvironmentVariable(TokenOptions.DefaultVariable, null);
        try
        {
            await Assert.ThrowsAsync<CliErrorException>(() => Run(new FakeRepoLister(), "--org", "acme"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenOptions.DefaultVariable, original);
        }
    }

    [Fact]
    public async Task Canceled_Propagates()
    {
        using var _ = Token("ghp_x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReposCommand.RunAsync(["--org", "acme", "--token-env", EnvVar], new FakeRepoLister(), new NullLog(), cts.Token));
    }
}
