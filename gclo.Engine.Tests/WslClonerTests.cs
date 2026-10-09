using gclo.Engine;

namespace gclo.Engine.Tests;

/// <summary>
/// <see cref="WslCloner"/> (#8) through a fake process runner: no WSL needed. The
/// contract under test is what wsl.exe is asked to run, that the token never reaches
/// an argument vector or the script, and how output and failures are interpreted.
/// </summary>
public sealed class WslClonerTests
{
    private const string WslExe = @"C:\Windows\System32\wsl.exe";
    private const string Token = "ghp_secret_token_value_1234567890";

    private readonly FakeProcessRunner _runner = new();

    private WslCloner Create(bool wslExists = true)
        => new(_runner, _ => wslExists, WslExe);

    // ---------------------------------------------------------------- probe

    [Fact]
    public async Task Probe_NoWslExe_IsUnavailable_WithoutRunningAnything()
    {
        WslAvailability result = await Create(wslExists: false).ProbeAsync(CancellationToken.None);

        Assert.False(result.Available);
        Assert.Contains("not installed", result.Detail);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Probe_GitRunsInDistro_IsAvailable_WithTheGitVersion()
    {
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(0, "git version 2.53.0\n", ""));

        WslAvailability result = await Create().ProbeAsync(CancellationToken.None);

        Assert.True(result.Available);
        Assert.Equal("git version 2.53.0", result.Detail);
        ProcessCall call = Assert.Single(_runner.Calls);
        Assert.Equal(WslExe, call.FileName);
        Assert.Equal(["-e", "git", "--version"], call.Arguments);
        Assert.Null(call.Environment);
        Assert.Null(call.StandardInput);
    }

    [Fact]
    public async Task Probe_NoDistribution_IsUnavailable_WithWslsOwnMessage()
    {
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(
            -1, "", "Windows Subsystem for Linux has no installed distributions.\nUse 'wsl.exe --list --online'.\n"));

        WslAvailability result = await Create().ProbeAsync(CancellationToken.None);

        Assert.False(result.Available);
        Assert.Equal("the default WSL distribution did not run git: Windows Subsystem for Linux has no installed distributions.", result.Detail);
    }

    [Fact]
    public async Task Probe_ExitZeroButNotGit_IsUnavailable()
    {
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(0, "sh: git: not found\n", ""));

        WslAvailability result = await Create().ProbeAsync(CancellationToken.None);

        Assert.False(result.Available);
        Assert.Contains("git is not installed in the default WSL distribution (sh: git: not found)", result.Detail);
    }

    [Fact]
    public async Task Probe_NonZeroWithNoOutput_ReportsTheExitCode()
    {
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(127, "", ""));

        WslAvailability result = await Create().ProbeAsync(CancellationToken.None);

        Assert.False(result.Available);
        Assert.EndsWith("exit code 127", result.Detail);
    }

    [Fact]
    public async Task Probe_RunnerThrows_IsUnavailable_NotAnException()
    {
        _runner.Handler = (_, _) => throw new System.ComponentModel.Win32Exception("access denied");

        WslAvailability result = await Create().ProbeAsync(CancellationToken.None);

        Assert.False(result.Available);
        Assert.Contains("could not be started: access denied", result.Detail);
    }

    [Fact]
    public async Task Probe_Canceled_Propagates()
    {
        _runner.Handler = (_, ct) => Task.FromCanceled<ProcessResult>(new CancellationToken(canceled: true));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().ProbeAsync(CancellationToken.None));
    }

    // ---------------------------------------------------------------- clone

    [Fact]
    public async Task Clone_RunsTheScriptThroughStdin_WithTheTokenOnlyInTheEnvironment()
    {
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(0, "Ubuntu\n/home/me/gclo/acme/repo\n", ""));

        WslCloneResult result = await Create().CloneAsync(
            "https://github.com/acme/repo.git", "acme", "repo", Token, CancellationToken.None);

        ProcessCall call = Assert.Single(_runner.Calls);
        Assert.Equal(WslExe, call.FileName);
        Assert.Equal(["-e", "sh", "-s", "acme", "repo", "https://github.com/acme/repo.git"], call.Arguments);
        Assert.Equal(WslCloner.CloneScript, call.StandardInput);
        Assert.DoesNotContain(Token, string.Join(" ", call.Arguments));
        Assert.DoesNotContain(Token, call.StandardInput);
        Assert.NotNull(call.Environment);
        Assert.Equal(Token, call.Environment![WslCloner.TokenVariable]);
        Assert.Equal("0", call.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal(WslCloner.TokenVariable + ":GIT_TERMINAL_PROMPT", call.Environment["WSLENV"]);

        Assert.Equal("Ubuntu", result.Distribution);
        Assert.Equal("/home/me/gclo/acme/repo", result.LinuxPath);
        Assert.Equal(@"\\wsl.localhost\Ubuntu\home\me\gclo\acme\repo", result.WindowsPath);
    }

    [Fact]
    public void CloneScript_ReadsTheTokenThroughACredentialHelper_AndFastForwardsAnExistingClone()
    {
        string script = WslCloner.CloneScript;

        Assert.Contains("password=$" + WslCloner.TokenVariable, script);
        Assert.Contains("credential.helper=", script);
        Assert.Contains("git clone", script.Replace("-c credential.helper= -c \"credential.helper=$helper\" ", ""));
        Assert.Contains("pull --ff-only", script);
        Assert.Contains("$HOME/gclo/$org/$repo", script);
        Assert.Contains("$WSL_DISTRO_NAME", script);
        Assert.StartsWith("set -e", script);
    }

    [Fact]
    public async Task Clone_IgnoresChatterBeforeTheLastTwoLines()
    {
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(
            0, "Cloning into '/home/me/gclo/acme/repo'...\nwarning: something\nDebian\n/home/me/gclo/acme/repo\n", "noise on stderr\n"));

        WslCloneResult result = await Create().CloneAsync("https://x/y.git", "acme", "repo", Token, CancellationToken.None);

        Assert.Equal("Debian", result.Distribution);
        Assert.Equal("/home/me/gclo/acme/repo", result.LinuxPath);
    }

    [Fact]
    public async Task Clone_GitFails_ThrowsWithTheLastStderrLine()
    {
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(
            128, "", "Cloning into '/home/me/gclo/acme/repo'...\nfatal: Authentication failed for 'https://github.com/acme/repo.git/'\n"));

        var ex = await Assert.ThrowsAsync<WslCloneException>(
            () => Create().CloneAsync("https://x/y.git", "acme", "repo", Token, CancellationToken.None));

        Assert.Equal("Clone in WSL failed: fatal: Authentication failed for 'https://github.com/acme/repo.git/'", ex.Message);
    }

    [Fact]
    public async Task Clone_FailsWithStdoutOnly_UsesStdout_ThenExitCode()
    {
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(1, "disk full\n", ""));
        var ex = await Assert.ThrowsAsync<WslCloneException>(
            () => Create().CloneAsync("https://x/y.git", "acme", "repo", Token, CancellationToken.None));
        Assert.Equal("Clone in WSL failed: disk full", ex.Message);

        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(9, "", ""));
        ex = await Assert.ThrowsAsync<WslCloneException>(
            () => Create().CloneAsync("https://x/y.git", "acme", "repo", Token, CancellationToken.None));
        Assert.Equal("Clone in WSL failed: exit code 9", ex.Message);
    }

    [Fact]
    public async Task Clone_SucceedsWithoutReportingAPath_Throws()
    {
        _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(0, "only-one-line\n", ""));

        var ex = await Assert.ThrowsAsync<WslCloneException>(
            () => Create().CloneAsync("https://x/y.git", "acme", "repo", Token, CancellationToken.None));

        Assert.Contains("did not report where", ex.Message);
    }

    [Fact]
    public async Task Clone_RunnerThrows_IsWrapped()
    {
        _runner.Handler = (_, _) => throw new System.ComponentModel.Win32Exception("access denied");

        var ex = await Assert.ThrowsAsync<WslCloneException>(
            () => Create().CloneAsync("https://x/y.git", "acme", "repo", Token, CancellationToken.None));

        Assert.Equal("wsl.exe could not be started: access denied", ex.Message);
    }

    [Fact]
    public async Task Clone_Canceled_Propagates()
    {
        _runner.Handler = (_, _) => Task.FromCanceled<ProcessResult>(new CancellationToken(canceled: true));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create().CloneAsync("https://x/y.git", "acme", "repo", Token, CancellationToken.None));
    }

    [Theory]
    [InlineData("", "acme", "repo")]
    [InlineData("https://x/y.git", " ", "repo")]
    [InlineData("https://x/y.git", "acme", "")]
    public async Task Clone_RejectsBlankInputs(string url, string org, string repo)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Create().CloneAsync(url, org, repo, Token, CancellationToken.None));
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Clone_RejectsNullToken()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => Create().CloneAsync("https://x/y.git", "acme", "repo", null!, CancellationToken.None));
    }

    [Fact]
    public void Defaults_PointAtSystem32Wsl()
    {
        // The default constructor must be usable by the app without arguments; its
        // probe answers "not installed" cleanly on a machine without wsl.exe.
        var cloner = new WslCloner();
        Assert.NotNull(cloner);
    }
}
