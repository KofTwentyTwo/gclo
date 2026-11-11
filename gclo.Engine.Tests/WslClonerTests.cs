/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

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
      Assert.Equal(["-e", "sh", "-s", "acme", "repo", "https://github.com/acme/repo.git", "github.com"], call.Arguments);
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
      Assert.Contains("git clone", script.Replace("-c credential.helper= -c \"credential.https://$host.helper=$helper\" ", ""));
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

      WslCloneException ex = await Assert.ThrowsAsync<WslCloneException>(
          () => Create().CloneAsync("https://x/y.git", "acme", "repo", Token, CancellationToken.None));

      Assert.Equal("Clone in WSL failed: fatal: Authentication failed for 'https://github.com/acme/repo.git/'", ex.Message);
   }



   [Fact]
   public async Task Clone_FailsWithStdoutOnly_UsesStdout_ThenExitCode()
   {
      _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(1, "disk full\n", ""));
      WslCloneException ex = await Assert.ThrowsAsync<WslCloneException>(
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

      WslCloneException ex = await Assert.ThrowsAsync<WslCloneException>(
          () => Create().CloneAsync("https://x/y.git", "acme", "repo", Token, CancellationToken.None));

      Assert.Contains("did not report where", ex.Message);
   }



   [Fact]
   public async Task Clone_RunnerThrows_IsWrapped()
   {
      _runner.Handler = (_, _) => throw new System.ComponentModel.Win32Exception("access denied");

      WslCloneException ex = await Assert.ThrowsAsync<WslCloneException>(
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
   public void CloneScript_ScopesTheHelperToTheUrlsHost_AndRefusesAFolderClonedFromElsewhere()
   {
      string script = WslCloner.CloneScript;

      // Every git invocation first clears inherited helpers, then installs the token
      // helper for the listed host only: nothing unscoped may remain (#139).
      Assert.Equal(2, CountOf(script, "-c credential.helper= -c \"credential.https://$host.helper=$helper\""));
      Assert.Equal(0, CountOf(script, "\"credential.helper=$helper\""));
      Assert.Contains("host=\"$4\"", script);
      // "--" ends option parsing, so a URL can never be read as an option. The
      // clone is made beside the destination and moved into place when it is
      // complete: a clone that is cut off leaves no folder that looks like one.
      Assert.Contains("clone --quiet -- \"$url\" \"$tmp\"", script);
      Assert.Contains("tmp=\"$HOME/gclo/.gclo-tmp/$org.$repo.$$\"", script);
      Assert.Contains("trap 'rm -rf \"$tmp\"' EXIT", script);
      Assert.True(
          script.IndexOf("clone --quiet", StringComparison.Ordinal) < script.IndexOf("mv -T \"$tmp\" \"$dest\"", StringComparison.Ordinal),
          "the folder is moved into place after the clone");
      Assert.True(
          script.IndexOf("mv -T \"$tmp\" \"$dest\"", StringComparison.Ordinal) < script.IndexOf("trap - EXIT", StringComparison.Ordinal),
          "the cleanup stays armed until the folder is in place");
      Assert.Contains("/gclo/" + WslCloner.TemporaryFolderName + "/", script);
      // What a killed run left is removed only from gclo's own temporary folder, and
      // only when it is old: a young one may be another gclo's clone in progress.
      Assert.Contains("find \"$HOME/gclo/.gclo-tmp\" -mindepth 1 -maxdepth 1 -mmin +1440 -exec rm -rf {} +", script);
      Assert.DoesNotContain("rm -rf \"$dest\"", script);
      // Something that is in the way and is not a repository is not cloned into.
      Assert.Contains("exists but is not a repository", script);
      // An existing destination is only fast-forwarded when it is a clone of this URL.
      // The stored URL is compared, not "remote get-url": that one applies the user's
      // url.*.insteadOf rewrites and would refuse a destination gclo itself cloned.
      Assert.Contains("config --get remote.origin.url)\" != \"$url\"", script);
      Assert.DoesNotContain("remote get-url", script);
      Assert.True(
          script.IndexOf("config --get remote.origin.url", StringComparison.Ordinal) < script.IndexOf("pull --ff-only", StringComparison.Ordinal),
          "the origin check must come before the pull");
   }



   [Fact]
   public async Task Clone_PassesTheHostWithItsPort_ForAServerOnANonDefaultPort()
   {
      _runner.Handler = (_, _) => Task.FromResult(new ProcessResult(0, "Ubuntu\n/home/me/gclo/acme/repo\n", ""));

      await Create().CloneAsync("https://git.example.com:3000/acme/repo.git", "acme", "repo", Token, CancellationToken.None);

      ProcessCall call = Assert.Single(_runner.Calls);
      Assert.Equal(["-e", "sh", "-s", "acme", "repo", "https://git.example.com:3000/acme/repo.git", "git.example.com:3000"], call.Arguments);
   }



   [Theory]
   [InlineData("../..", "repo")]
   [InlineData("..", "repo")]
   [InlineData("acme/other", "repo")]
   [InlineData("acme", "../../.ssh")]
   [InlineData("acme", "re/po")]
   [InlineData("acme", @"re\po")]
   public async Task Clone_NameThatWouldLeaveTheGcloFolder_IsRefused_WithoutStartingWsl(string organization, string repository)
   {
      WslCloneException ex = await Assert.ThrowsAsync<WslCloneException>(
          () => Create().CloneAsync("https://github.com/acme/repo.git", organization, repository, Token, CancellationToken.None));

      Assert.StartsWith("Clone in WSL refused: '", ex.Message);
      Assert.Contains("cannot be used as a folder name", ex.Message);
      Assert.Empty(_runner.Calls);
   }



   [Theory]
   [InlineData(".gclo-tmp")]
   [InlineData(".GCLO-TMP")]
   public async Task Clone_IntoGclosOwnTemporaryFolder_IsRefused_WithoutStartingWsl(string organization)
   {
      // Unfinished clones are made in ~/gclo/.gclo-tmp, and old ones there are removed.
      WslCloneException ex = await Assert.ThrowsAsync<WslCloneException>(
          () => Create().CloneAsync("https://github.com/acme/repo.git", organization, "repo", Token, CancellationToken.None));

      Assert.Contains("gclo's own temporary folder", ex.Message);
      Assert.Empty(_runner.Calls);
   }



   [Theory]
   [InlineData("http://github.com/acme/repo.git")]
   [InlineData("ssh://git@github.com/acme/repo.git")]
   [InlineData("git@github.com:acme/repo.git")]
   [InlineData("--upload-pack=touch /tmp/x")]
   [InlineData(@"C:\src\acme\repo")]
   public async Task Clone_UrlThatIsNotHttps_IsRefused_WithoutStartingWsl(string url)
   {
      WslCloneException ex = await Assert.ThrowsAsync<WslCloneException>(
          () => Create().CloneAsync(url, "acme", "repo", Token, CancellationToken.None));

      Assert.Equal("Clone in WSL refused: the clone URL is not an HTTPS URL.", ex.Message);
      Assert.Empty(_runner.Calls);
   }



   private static int CountOf(string text, string fragment)
       => (text.Length - text.Replace(fragment, "", StringComparison.Ordinal).Length) / fragment.Length;



   [Fact]
   public void Defaults_PointAtSystem32Wsl()
   {
      // The default constructor must be usable by the app without arguments; its
      // probe answers "not installed" cleanly on a machine without wsl.exe.
      var cloner = new WslCloner();
      Assert.NotNull(cloner);
   }
}
