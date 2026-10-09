using gclo.Engine;
using gclo.ViewModels;

namespace gclo.Cli.Tests;

/// <summary>
/// Covers 'gclo sync' end to end over fake collaborators: argument and account
/// resolution, token resolution, the sync run and its exit codes, JSON/quiet
/// output, path sanitization, and account result recording.
/// </summary>
public sealed class SyncCommandTests : IDisposable
{
    private const string EnvVar = "GCLO_SYNC_TEST_TOKEN";

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "gclo-cli-tests", Guid.NewGuid().ToString("N"));
    private readonly InMemoryVault _vault = new();
    private AccountsStore? _store;

    public SyncCommandTests()
    {
        Environment.SetEnvironmentVariable(EnvVar, "ghp_test");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Target => Path.Combine(_dir, "target");
    private AccountsStore Store => _store ??= new AccountsStore(_vault, _dir, new NullLog());
    private Func<IActivityLog, (AccountsStore, ITokenVault)> Open => _ => (Store, _vault);

    private static RepoDescriptor Repo(string name) => new(name, $"https://x/{name}.git", "main", false);

    private Task<int> Run(FakeRepoLister lister, FakeGit git, params string[] args)
        => SyncCommand.RunAsync(args, lister, git, Open, new NullLog(), CancellationToken.None);

    private Account Seed(string name, string org, bool orgSubfolder = false, int parallel = 8)
    {
        Store.Save(
            new Account
            {
                Id = Guid.NewGuid(),
                Name = name,
                Organization = org,
                TargetRoot = Target,
                CreateOrgSubfolder = orgSubfolder,
                MaxConcurrency = parallel,
            },
            "tok-" + name);
        return Store.FindByName(name)!;
    }

    // ---------------------------------------------------------------- parsing

    [Fact]
    public async Task Help_PrintsUsage()
    {
        using var console = new ConsoleCapture();

        int code = await Run(new FakeRepoLister(), new FakeGit(), "--help");

        Assert.Equal(0, code);
        Assert.Contains("Usage: gclo sync", console.Out);
    }

    [Fact]
    public async Task MissingOrg_Throws()
        => await Assert.ThrowsAsync<CliUsageException>(
            () => Run(new FakeRepoLister(), new FakeGit(), "--target", Target));

    [Fact]
    public async Task MissingTarget_Throws()
        => await Assert.ThrowsAsync<CliUsageException>(
            () => Run(new FakeRepoLister(), new FakeGit(), "--org", "acme"));

    [Theory]
    [InlineData("0")]
    [InlineData("65")]
    [InlineData("abc")]
    public async Task ParallelOutsideTheSupportedRange_IsRejected(string value)
    {
        var ex = await Assert.ThrowsAsync<CliUsageException>(
            () => Run(new FakeRepoLister(), new FakeGit(), "--org", "acme", "--target", Target, "--parallel", value));
        Assert.Contains("between 1 and 64", ex.Message);
    }

    [Fact]
    public async Task JsonAndJsonLines_AreMutuallyExclusive()
    {
        var ex = await Assert.ThrowsAsync<CliUsageException>(
            () => Run(new FakeRepoLister(), new FakeGit(), "--org", "acme", "--target", Target, "--json", "--json-lines"));
        Assert.Contains("not both", ex.Message);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("0")]
    [InlineData("-3")]
    public async Task BadParallel_Throws(string value)
        => await Assert.ThrowsAsync<CliUsageException>(
            () => Run(new FakeRepoLister(), new FakeGit(), "--org", "acme", "--target", Target, "--parallel", value));

    [Fact]
    public async Task UnknownOption_Throws()
        => await Assert.ThrowsAsync<CliUsageException>(
            () => Run(new FakeRepoLister(), new FakeGit(), "--nope"));

    // ---------------------------------------------------------------- sync runs

    [Fact]
    public async Task AllRepositoriesClone_ReturnsZero_AndPrintsSummary()
    {
        using var console = new ConsoleCapture();
        var lister = new FakeRepoLister { Result = [Repo("a"), Repo("b")] };
        var git = new FakeGit(); // clone succeeds by default

        int code = await Run(lister, git, "--org", "acme", "--target", Target, "--token-env", EnvVar);

        Assert.Equal(0, code);
        Assert.Equal(["a", "b"], git.ClonedRepoNames.OrderBy(n => n));
        Assert.Contains("Finished:", console.Out);
        Assert.Contains("2 cloned", console.Out);
    }

    [Fact]
    public async Task SomeRepositoriesFail_ReturnsOne()
    {
        using var console = new ConsoleCapture();
        var lister = new FakeRepoLister { Result = [Repo("a"), Repo("b")] };
        var git = new FakeGit
        {
            OnClone = (_, path, _, _, _) => Path.GetFileName(path) == "b"
                ? Task.FromException(new InvalidOperationException("boom"))
                : Task.CompletedTask,
        };

        int code = await Run(lister, git, "--org", "acme", "--target", Target, "--token-env", EnvVar);

        Assert.Equal(1, code);
        Assert.Contains("1 failed", console.Out);
        Assert.Contains("b  Failed  boom", console.Error);
    }

    [Fact]
    public async Task Quiet_SuppressesTransitionsButKeepsSummaryAndFailures()
    {
        using var console = new ConsoleCapture();
        var lister = new FakeRepoLister { Result = [Repo("a")] };

        int code = await Run(lister, new FakeGit(), "--org", "acme", "--target", Target, "--token-env", EnvVar, "--quiet");

        Assert.Equal(0, code);
        Assert.DoesNotContain("a  Cloning", console.Out);
        Assert.Contains("Finished:", console.Out);
    }

    [Fact]
    public async Task Json_PrintsMachineReadableSummary()
    {
        using var console = new ConsoleCapture();
        var lister = new FakeRepoLister { Result = [Repo("a")] };

        int code = await Run(lister, new FakeGit(), "--org", "acme", "--target", Target, "--token-env", EnvVar, "--json");

        Assert.Equal(0, code);
        Assert.Contains("\"total\":1", console.Out);
        Assert.Contains("\"cloned\":1", console.Out);
        Assert.Contains("\"failures\":[]", console.Out);
    }

    [Fact]
    public async Task ValidParallel_IsAccepted()
    {
        var lister = new FakeRepoLister { Result = [Repo("a")] };

        int code = await Run(lister, new FakeGit(), "--org", "acme", "--target", Target, "--token-env", EnvVar, "--parallel", "4");

        Assert.Equal(0, code);
    }

    [Fact]
    public async Task TargetFolderUnusable_BecomesCliError()
    {
        // A file sits where the target directory should be, so the engine's
        // Directory.CreateDirectory throws — surfaced as a target-folder CliError.
        string target = Path.Combine(_dir, "target-as-file");
        File.WriteAllText(target, "not a directory");
        var lister = new FakeRepoLister { Result = [Repo("a")] };

        var ex = await Assert.ThrowsAsync<CliErrorException>(
            () => Run(lister, new FakeGit(), "--org", "acme", "--target", target, "--token-env", EnvVar));
        Assert.Contains("Cannot use target folder", ex.Message);
    }

    [Fact]
    public async Task Account_RecordResultFails_WarnsButStillSucceeds()
    {
        using var console = new ConsoleCapture();
        Seed("work", "acme");
        // Block the store's atomic write: a directory where its temp file must go.
        Directory.CreateDirectory(Path.Combine(_dir, "accounts.json.tmp"));
        var lister = new FakeRepoLister { Result = [Repo("a")] };

        int code = await Run(lister, new FakeGit(), "--account", "work");

        Assert.Equal(0, code); // the repos are on disk; a bookkeeping failure must not change that
        Assert.Contains("could not record the sync result", console.Error);
    }

    [Fact]
    public async Task SanitizePaths_CanceledBeforeRecovery_LeavesRepoFailed()
    {
        using var console = new ConsoleCapture();
        using var cts = new CancellationTokenSource();
        var lister = new FakeRepoLister { Result = [Repo("a")] };
        var git = new FakeGit
        {
            OnClone = (_, _, _, _, _) =>
            {
                cts.Cancel(); // cancellation lands during the run, before sanitization
                return Task.FromException(InvalidPaths());
            },
        };

        int code = await SyncCommand.RunAsync(
            ["--org", "acme", "--target", Target, "--token-env", EnvVar, "--sanitize-paths"],
            lister, git, Open, new NullLog(), cts.Token);

        Assert.Equal(1, code);
        Assert.Empty(git.RecoveredRepoNames); // recovery was never attempted
    }

    [Fact]
    public async Task ListerAccessRefusal_MapsToTheExitCodeTaxonomy()
    {
        var unauthorized = new FakeRepoLister
        {
            Throw = new GitHubAccessException(GitHubAccessKind.Unauthorized, "GitHub rejected the token (401)."),
        };
        var ex = await Assert.ThrowsAsync<CliErrorException>(
            () => Run(unauthorized, new FakeGit(), "--org", "acme", "--target", Target, "--token-env", EnvVar));
        Assert.Equal(ExitCodes.Auth, ex.ExitCode);

        var forbidden = new FakeRepoLister
        {
            Throw = new GitHubAccessException(GitHubAccessKind.Forbidden, "'acme' is an organization, but this token cannot see it."),
        };
        ex = await Assert.ThrowsAsync<CliErrorException>(
            () => Run(forbidden, new FakeGit(), "--org", "acme", "--target", Target, "--token-env", EnvVar));
        Assert.Equal(ExitCodes.Auth, ex.ExitCode);

        var throttled = new FakeRepoLister
        {
            Throw = new GitHubAccessException(GitHubAccessKind.RateLimited, "rate limit exceeded"),
        };
        ex = await Assert.ThrowsAsync<CliErrorException>(
            () => Run(throttled, new FakeGit(), "--org", "acme", "--target", Target, "--token-env", EnvVar));
        Assert.Equal(ExitCodes.Transient, ex.ExitCode);

        var missing = new FakeRepoLister
        {
            Throw = new GitHubAccessException(GitHubAccessKind.NotFound, "'ghost' was found neither as an organization nor as a user account (404)."),
        };
        ex = await Assert.ThrowsAsync<CliErrorException>(
            () => Run(missing, new FakeGit(), "--org", "ghost", "--target", Target, "--token-env", EnvVar));
        Assert.Equal(ExitCodes.Fatal, ex.ExitCode);
    }

    // ---------------------------------------------------------------- selection

    [Fact]
    public async Task IncludeExcludeAndSkipArchived_NarrowTheSelection()
    {
        using var console = new ConsoleCapture();
        var lister = new FakeRepoLister
        {
            Result =
            [
                Repo("platform-api"), Repo("platform-web"), Repo("platform-legacy"), Repo("docs"),
                new RepoDescriptor("platform-old", "https://x/platform-old.git", "main", IsArchived: true),
            ],
        };
        var git = new FakeGit();

        int code = await Run(lister, git, "--org", "acme", "--target", Target, "--token-env", EnvVar,
            "--include", "platform-*", "--exclude", "*-legacy", "--skip-archived");

        Assert.Equal(0, code);
        Assert.Equal(["platform-api", "platform-web"], git.ClonedRepoNames.OrderBy(n => n));
        Assert.Contains("2 cloned", console.Out);
        Assert.Contains("of 2.", console.Out); // the summary counts the selection, not the listing
    }

    [Fact]
    public async Task EmptyIncludePattern_IsAUsageError()
    {
        var ex = await Assert.ThrowsAsync<CliUsageException>(
            () => Run(new FakeRepoLister(), new FakeGit(), "--org", "acme", "--target", Target, "--include", "  "));
        Assert.Contains("--include expects", ex.Message);
    }

    [Fact]
    public async Task DryRun_ListsActionsAndTouchesNothing()
    {
        using var console = new ConsoleCapture();
        var lister = new FakeRepoLister { Result = [Repo("new-one"), Repo("existing")] };
        var git = new FakeGit { IsValid = path => Path.GetFileName(path) == "existing" };

        int code = await Run(lister, git, "--org", "acme", "--target", Target, "--token-env", EnvVar, "--dry-run");

        Assert.Equal(0, code);
        Assert.Empty(git.ClonedRepoNames);
        Assert.Contains("new-one  would clone", console.Out);
        Assert.Contains("existing  would update", console.Out);
        Assert.Contains("Dry run: 1 would clone, 1 would update of 2.", console.Out);
    }

    [Fact]
    public async Task DryRun_Json_PrintsAnArrayOfActions()
    {
        using var console = new ConsoleCapture();
        var lister = new FakeRepoLister { Result = [Repo("new-one")] };

        int code = await Run(lister, new FakeGit(), "--org", "acme", "--target", Target, "--token-env", EnvVar, "--dry-run", "--json");

        Assert.Equal(0, code);
        Assert.Equal("[{\"repo\":\"new-one\",\"action\":\"clone\"}]", console.Out.Trim());
    }

    // ---------------------------------------------------------------- machine-readable output

    [Fact]
    public async Task JsonLines_PrintsOneObjectPerTransition_ThenTheSummary()
    {
        using var console = new ConsoleCapture();
        var lister = new FakeRepoLister { Result = [Repo("a"), Repo("b")] };
        var git = new FakeGit
        {
            OnClone = (_, path, _, _, _) => Path.GetFileName(path) == "b"
                ? Task.FromException(new InvalidOperationException("boom"))
                : Task.CompletedTask,
        };

        int code = await Run(lister, git, "--org", "acme", "--target", Target, "--token-env", EnvVar, "--json-lines", "--parallel", "1");

        Assert.Equal(1, code);
        string[] lines = console.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        Assert.All(lines, l => Assert.StartsWith("{", l));
        Assert.Contains("{\"type\":\"progress\",\"repo\":\"a\",\"status\":\"Queued\",\"error\":null}", lines);
        Assert.Contains("{\"type\":\"progress\",\"repo\":\"a\",\"status\":\"Done\",\"error\":null}", lines);
        Assert.Contains("{\"type\":\"progress\",\"repo\":\"b\",\"status\":\"Failed\",\"error\":\"boom\"}", lines);
        Assert.StartsWith("{\"type\":\"summary\",\"total\":2,\"cloned\":1,\"updated\":0,\"failed\":1", lines[^1]);
        Assert.Equal("", console.Error); // failures live in the stream, not on stderr
    }

    [Fact]
    public async Task Json_CarriesSanitizedRepos_AndFullInvalidPathLists_WithNoStderrNoise()
    {
        using var console = new ConsoleCapture();
        var lister = new FakeRepoLister { Result = [Repo("fixable"), Repo("stuck")] };
        var git = new FakeGit
        {
            OnClone = (_, path, _, _, _) => Task.FromException(InvalidPaths()),
            OnApplyRecovery = (path, _, _) => Path.GetFileName(path) == "stuck"
                ? Task.FromException(new IOException("disk full"))
                : Task.CompletedTask,
        };

        int code = await Run(lister, git, "--org", "acme", "--target", Target, "--token-env", EnvVar, "--json", "--sanitize-paths", "--parallel", "1");

        Assert.Equal(1, code);
        Assert.Equal("", console.Error); // the 'Sanitized' note and path details stay out of stderr in JSON mode
        string json = console.Out.Trim();
        Assert.StartsWith("{\"type\":\"summary\"", json);
        Assert.Contains("\"sanitized\":[{\"repo\":\"fixable\",\"renamed\":1,\"skipped\":1,\"skippedPaths\":[\"dup\"]}]", json);
        Assert.Contains("\"repo\":\"stuck\"", json);
        Assert.Contains("path sanitization failed: disk full", json);
        Assert.Contains("\"invalidPaths\":null", json); // a non-path failure has no list
    }

    [Fact]
    public async Task Json_FailureFromInvalidPaths_CarriesEveryOffendingPath()
    {
        using var console = new ConsoleCapture();
        var many = Enumerable.Range(0, 12)
            .Select(i => new InvalidPathInfo($"bad{i}:x.txt", "invalid on Windows", $"bad{i}_x.txt"))
            .ToList();
        var lister = new FakeRepoLister { Result = [Repo("a")] };
        var git = new FakeGit { OnClone = (_, _, _, _, _) => Task.FromException(new InvalidRepositoryPathsException(many)) };

        int code = await Run(lister, git, "--org", "acme", "--target", Target, "--token-env", EnvVar, "--json");

        Assert.Equal(1, code);
        Assert.Contains("\"path\":\"bad11:x.txt\",\"reason\":\"invalid on Windows\",\"suggestedName\":\"bad11_x.txt\"", console.Out);
    }

    [Fact]
    public async Task ListerInvalidOperation_BecomesCliError()
    {
        var lister = new FakeRepoLister { Throw = new InvalidOperationException("org not found (404)") };

        var ex = await Assert.ThrowsAsync<CliErrorException>(
            () => Run(lister, new FakeGit(), "--org", "ghost", "--target", Target, "--token-env", EnvVar));
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task Canceled_ReturnsOne()
    {
        var lister = new FakeRepoLister { Throw = new OperationCanceledException() };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        int code = await SyncCommand.RunAsync(
            ["--org", "acme", "--target", Target, "--token-env", EnvVar],
            lister, new FakeGit(), Open, new NullLog(), cts.Token);

        Assert.Equal(1, code);
    }

    // ---------------------------------------------------------------- accounts

    [Fact]
    public async Task Account_SeedsOrgTargetAndParallel()
    {
        using var console = new ConsoleCapture();
        Seed("work", "acme", orgSubfolder: false, parallel: 3);
        var lister = new FakeRepoLister { Result = [Repo("a")] };

        int code = await Run(lister, new FakeGit(), "--account", "work");

        Assert.Equal(0, code);
        Assert.Contains("Finished:", console.Out);
        // The account's stored token was used (no --token option given).
        Assert.NotNull(Store.FindByName("work")!.LastSyncSummary);
    }

    [Fact]
    public async Task Account_WithOrgSubfolder_NestsTheTarget()
    {
        Seed("work", "acme", orgSubfolder: true);
        var git = new FakeGit();
        var lister = new FakeRepoLister { Result = [Repo("a")] };

        int code = await Run(lister, git, "--account", "work");

        Assert.Equal(0, code);
        // Target became <root>\acme\a.
        Assert.Contains("a", git.ClonedRepoNames);
        Assert.True(Directory.Exists(Path.Combine(Target, "acme")));
    }

    [Fact]
    public async Task UnknownAccount_NoAccounts_Throws()
    {
        var ex = await Assert.ThrowsAsync<CliErrorException>(
            () => Run(new FakeRepoLister(), new FakeGit(), "--account", "ghost"));
        Assert.Contains("No accounts exist yet", ex.Message);
    }

    [Fact]
    public async Task UnknownAccount_WithOtherAccounts_ListsThem()
    {
        Seed("work", "acme");

        var ex = await Assert.ThrowsAsync<CliErrorException>(
            () => Run(new FakeRepoLister(), new FakeGit(), "--account", "ghost"));
        Assert.Contains("Available accounts: work", ex.Message);
    }

    [Fact]
    public async Task Account_MissingVaultToken_Throws()
    {
        // Save metadata, then delete the vault entry so no token is retrievable.
        Account account = Seed("work", "acme");
        _vault.Delete(account.Id);

        var ex = await Assert.ThrowsAsync<CliErrorException>(
            () => Run(new FakeRepoLister(), new FakeGit(), "--account", "work"));
        Assert.Contains("no token", ex.Message);
    }

    [Fact]
    public async Task Account_ExplicitTokenOption_OverridesVault()
    {
        Account account = Seed("work", "acme");
        _vault.Delete(account.Id); // vault empty, but --token-env supplies one
        var lister = new FakeRepoLister { Result = [Repo("a")] };

        int code = await Run(lister, new FakeGit(), "--account", "work", "--token-env", EnvVar);

        Assert.Equal(0, code); // did not fall through to the missing-vault-token error
    }

    // ---------------------------------------------------------------- sanitize-paths

    private static InvalidRepositoryPathsException InvalidPaths() => new(
    [
        new InvalidPathInfo("bad:name.txt", "contains a character that is invalid on Windows", "bad_name.txt"),
        new InvalidPathInfo("dup", "differs only by case from 'DUP'", null), // no suggestion => skipped
    ]);

    [Fact]
    public async Task SanitizePaths_RecoversAnInvalidPathRepo_AndCountsItCloned()
    {
        using var console = new ConsoleCapture();
        var lister = new FakeRepoLister { Result = [Repo("a")] };
        var git = new FakeGit
        {
            OnClone = (_, _, _, _, _) => Task.FromException(InvalidPaths()),
            OnApplyRecovery = (_, _, _) => Task.CompletedTask, // recovery succeeds
        };

        int code = await Run(lister, git, "--org", "acme", "--target", Target, "--token-env", EnvVar, "--sanitize-paths");

        Assert.Equal(0, code);
        Assert.Contains("a", git.RecoveredRepoNames);
        Assert.Contains("with sanitized paths", console.Out);
        Assert.Contains("Sanitized", console.Error);
    }

    [Fact]
    public async Task SanitizePaths_RecoveryFails_LeavesRepoFailed()
    {
        using var console = new ConsoleCapture();
        var lister = new FakeRepoLister { Result = [Repo("a")] };
        var git = new FakeGit
        {
            OnClone = (_, _, _, _, _) => Task.FromException(InvalidPaths()),
            OnApplyRecovery = (_, _, _) => Task.FromException(new IOException("disk full")),
        };

        int code = await Run(lister, git, "--org", "acme", "--target", Target, "--token-env", EnvVar, "--sanitize-paths");

        Assert.Equal(1, code);
        Assert.Contains("path sanitization failed", console.Error);
    }

    [Fact]
    public async Task InvalidPathFailure_WithoutSanitize_PrintsOffendingPaths()
    {
        using var console = new ConsoleCapture();
        var lister = new FakeRepoLister { Result = [Repo("a")] };
        var git = new FakeGit { OnClone = (_, _, _, _, _) => Task.FromException(InvalidPaths()) };

        int code = await Run(lister, git, "--org", "acme", "--target", Target, "--token-env", EnvVar);

        Assert.Equal(1, code);
        Assert.Contains("bad:name.txt", console.Error);
        Assert.Contains("invalid on Windows", console.Error);
    }

    [Fact]
    public async Task InvalidPathFailure_ManyPaths_TruncatesTheListing()
    {
        using var console = new ConsoleCapture();
        var many = Enumerable.Range(0, 15)
            .Select(i => new InvalidPathInfo($"bad{i}:x.txt", "invalid on Windows", $"bad{i}_x.txt"))
            .ToList();
        var lister = new FakeRepoLister { Result = [Repo("a")] };
        var git = new FakeGit
        {
            OnClone = (_, _, _, _, _) => Task.FromException(new InvalidRepositoryPathsException(many)),
        };

        int code = await Run(lister, git, "--org", "acme", "--target", Target, "--token-env", EnvVar);

        Assert.Equal(1, code);
        Assert.Contains("and 5 more invalid paths", console.Error);
    }
}
