/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using LibGit2Sharp;
using Xunit.Abstractions;
using static gclo.Engine.Tests.GitTestHelpers;


namespace gclo.Engine.Tests;


/// <summary>
/// Runs <see cref="WslCloner.CloneScript"/> for real, with a POSIX shell and local
/// repositories, where the machine has one (Git for Windows brings <c>sh</c>): what
/// the script leaves on disk is what matters, and matching its text cannot show
/// that. On a machine with no <c>sh</c> that can find <c>git</c> the tests have
/// nothing to run: they pass, and say so in their output.
/// </summary>
public sealed class WslCloneScriptRunTests : IDisposable
{
   private static readonly (string Sh, string Git)? s_tools = FindTools();

   private readonly string _root = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

   private readonly string _home;

   private readonly string _origin;



   private readonly ITestOutputHelper _output;



   public WslCloneScriptRunTests(ITestOutputHelper output)
   {
      _output = output;
      _home = Path.Combine(_root, "home");
      Directory.CreateDirectory(_home);

      // An origin with one commit, made through a working copy.
      _origin = Path.Combine(_root, "origin.git");
      Repository.Init(_origin, isBare: true);
      string seeder = Repository.Clone(_origin, Path.Combine(_root, "seeder"));
      using var repo = new Repository(seeder);
      File.WriteAllText(Path.Combine(repo.Info.WorkingDirectory, "readme.txt"), "theirs");
      Commands.Stage(repo, "readme.txt");
      Signature who = MakeSignature();
      repo.Commit("first commit", who, who);
      repo.Network.Push(repo.Network.Remotes["origin"], repo.Head.CanonicalName, new PushOptions());
   }



   public void Dispose() => TryDeleteDirectory(_root);



   /// <summary>
   /// A POSIX shell that can find git, or null when this machine has none. Looked for
   /// on the path and, because Git for Windows puts only its <c>cmd</c> folder there,
   /// beside every <c>git.exe</c> on the path and where Git for Windows installs.
   /// </summary>
   /// <remarks>
   /// Git for Windows' <c>bin\sh.exe</c> is a launcher: it puts git's own folders in
   /// front of the path before it starts the shell, and a stand-in for git that a
   /// test puts on the path is then never found. The shell it starts
   /// (<c>usr\bin\sh.exe</c>) is used instead, with its own folder on the path for
   /// the tools a script needs (<see cref="PathWith"/>). Which of the two a test run
   /// finds first depends on where it was started from.
   /// </remarks>
   private static (string Sh, string Git)? FindTools()
   {
      string shell = OperatingSystem.IsWindows() ? "sh.exe" : "sh";
      string[] path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
      var candidates = new List<string>(path.Select(folder => Path.Combine(folder, shell)));
      foreach(string folder in path.Where(folder => File.Exists(Path.Combine(folder, "git.exe"))))
      {
         candidates.Add(Path.GetFullPath(Path.Combine(folder, "..", "bin", shell)));
         candidates.Add(Path.GetFullPath(Path.Combine(folder, "..", "usr", "bin", shell)));
      }
      foreach(Environment.SpecialFolder programs in (Environment.SpecialFolder[])[Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86])
      {
         candidates.Add(Path.Combine(Environment.GetFolderPath(programs), "Git", "bin", shell));
      }

      foreach(string found in candidates.Where(File.Exists))
      {
         string started = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(found)!, "..", "usr", "bin", shell));
         string sh = OperatingSystem.IsWindows() && File.Exists(started) ? started : found;
         try
         {
            var ask = new ProcessStartInfo(sh) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            ask.Environment["PATH"] = PathWith(sh, null);
            ask.ArgumentList.Add("-c");
            ask.ArgumentList.Add("command -v git");
            using Process process = Process.Start(ask)!;
            string git = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(30000);
            if(process.ExitCode == 0 && git.Length > 0)
            {
               return (sh, git);
            }
         }
         catch(Exception)
         {
            // Not a shell that runs here: try the next one.
         }
      }
      return null;
   }



   /// <summary>
   /// The path a shell is run with: <paramref name="first"/> (a test's stand-ins,
   /// when it has any), the shell's own folder, then the path of this process.
   /// </summary>
   private static string PathWith(string sh, string? first)
       => string.Join(
           Path.PathSeparator,
           new[] { first, Path.GetDirectoryName(sh), Environment.GetEnvironmentVariable("PATH") }.Where(part => !string.IsNullOrEmpty(part)));



   /// <summary>False, with a line in the test's output, when this machine has no shell to run the script with.</summary>
   private bool CanRun()
   {
      if(s_tools is null)
      {
         _output.WriteLine("Not run: no 'sh' that can find 'git' on this machine (Git for Windows brings one).");
      }
      return s_tools is not null;
   }



   private string Destination(string org, string repo) => Path.Combine(_home, "gclo", org, repo);



   private string[] Leftovers()
   {
      string tmp = Path.Combine(_home, "gclo", ".gclo-tmp");
      return Directory.Exists(tmp) ? [.. Directory.GetFileSystemEntries(tmp).Select(Path.GetFileName).Order(StringComparer.Ordinal)!] : [];
   }



   /// <summary>Runs the script as wsl.exe would hand it to the shell: on standard input, with its four arguments.</summary>
   private (int ExitCode, string Output, string Error) Run(string org, string repo, string? url = null, string? pathPrefix = null, string? appear = null)
   {
      var start = new ProcessStartInfo(s_tools!.Value.Sh)
      {
         RedirectStandardInput = true,
         RedirectStandardOutput = true,
         RedirectStandardError = true,
         UseShellExecute = false,
      };
      foreach(string argument in (string[])["-s", "--", org, repo, (url ?? _origin).Replace('\\', '/'), "example.test"])
      {
         start.ArgumentList.Add(argument);
      }
      start.Environment["HOME"] = _home.Replace('\\', '/');
      start.Environment[WslCloner.TokenVariable] = "not-a-token";
      start.Environment["WSL_DISTRO_NAME"] = "test";
      start.Environment["GCLO_TEST_REAL_GIT"] = s_tools.Value.Git;
      start.Environment["GCLO_TEST_APPEAR"] = (appear ?? "").Replace('\\', '/');
      start.Environment["PATH"] = PathWith(s_tools.Value.Sh, pathPrefix);

      using Process process = Process.Start(start)!;
      process.StandardInput.Write(WslCloner.CloneScript);
      process.StandardInput.Close();
      Task<string> error = process.StandardError.ReadToEndAsync();
      string output = process.StandardOutput.ReadToEnd();
      Assert.True(process.WaitForExit(120000), "the script did not end");
      return (process.ExitCode, output, error.GetAwaiter().GetResult());
   }



   [Fact]
   public void ANewClone_EndsUpAtTheDestination_AndLeavesNothingBehind_AndASecondRunUpdatesIt()
   {
      if(!CanRun())
      {
         return;
      }

      (int exitCode, string output, string error) = Run("acme", "repo");

      Assert.True(exitCode == 0, error);
      Assert.Equal("theirs", File.ReadAllText(Path.Combine(Destination("acme", "repo"), "readme.txt")));
      Assert.Empty(Leftovers());
      Assert.EndsWith("/gclo/acme/repo", output.TrimEnd().Split('\n')[^1].TrimEnd(), StringComparison.Ordinal);

      (int again, _, string secondError) = Run("acme", "repo");
      Assert.True(again == 0, secondError);
   }



   [Fact]
   public void ACloneThatFails_LeavesNoFolderThatLooksLikeAClone()
   {
      if(!CanRun())
      {
         return;
      }

      (int exitCode, _, _) = Run("acme", "repo", url: Path.Combine(_root, "no-such-origin.git"));

      Assert.NotEqual(0, exitCode);
      Assert.False(Directory.Exists(Destination("acme", "repo")));
      Assert.Empty(Leftovers());
   }



   [Fact]
   public void AFolderWithSomethingInIt_AtTheDestination_IsRefused_AndAnEmptyOneIsUsed()
   {
      if(!CanRun())
      {
         return;
      }
      Directory.CreateDirectory(Destination("acme", "full"));
      File.WriteAllText(Path.Combine(Destination("acme", "full"), "mine.txt"), "mine");
      Directory.CreateDirectory(Destination("acme", "empty"));

      (int refused, _, string error) = Run("acme", "full");
      (int used, _, string usedError) = Run("acme", "empty");

      Assert.Equal(1, refused);
      Assert.Contains("exists but is not a repository", error, StringComparison.Ordinal);
      Assert.Equal("mine", File.ReadAllText(Path.Combine(Destination("acme", "full"), "mine.txt")));
      Assert.False(Directory.Exists(Path.Combine(Destination("acme", "full"), ".git")));
      Assert.True(used == 0, usedError);
      Assert.Equal("theirs", File.ReadAllText(Path.Combine(Destination("acme", "empty"), "readme.txt")));
   }



   [Fact]
   public void ADestinationThatAppearsWhileTheCloneRuns_IsNotMovedInto()
   {
      if(!CanRun())
      {
         return;
      }

      // A stand-in for git that runs the real one and then, after a clone, makes
      // the destination appear: what another gclo cloning the same repository does.
      string wrappers = Path.Combine(_root, "wrappers");
      Directory.CreateDirectory(wrappers);
      File.WriteAllText(
          Path.Combine(wrappers, "git"),
          "#!/bin/sh\n\"$GCLO_TEST_REAL_GIT\" \"$@\"\nrc=$?\n"
          + "case \" $* \" in *\" clone \"*) mkdir -p \"$GCLO_TEST_APPEAR\"; echo theirs-too > \"$GCLO_TEST_APPEAR/other.txt\";; esac\n"
          + "exit $rc\n");
      string destination = Destination("acme", "repo");

      (int exitCode, _, string error) = Run("acme", "repo", pathPrefix: wrappers, appear: destination);

      Assert.Equal(1, exitCode);
      Assert.Contains("appeared while the repository was being cloned", error, StringComparison.Ordinal);
      Assert.Equal(["other.txt"], Directory.GetFileSystemEntries(destination).Select(Path.GetFileName), StringComparer.Ordinal); // no clone nested inside it
      Assert.Empty(Leftovers());
   }



   [Fact]
   public void OnlyOldLeftovers_InGclosOwnTemporaryFolder_AreRemoved()
   {
      if(!CanRun())
      {
         return;
      }
      string tmp = Path.Combine(_home, "gclo", ".gclo-tmp");
      string old = Directory.CreateDirectory(Path.Combine(tmp, "acme.other.111")).FullName;
      File.WriteAllText(Path.Combine(old, "half.txt"), "x");
      Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
      Directory.CreateDirectory(Path.Combine(tmp, "acme.other.222")); // young: perhaps a clone in progress

      // A real clone whose name looks like a leftover's, and is old: not gclo's to remove.
      string sibling = Directory.CreateDirectory(Destination("acme", "repo.gclo-clone.keep")).FullName;
      File.WriteAllText(Path.Combine(sibling, "kept.txt"), "mine");
      Directory.SetLastWriteTimeUtc(sibling, DateTime.UtcNow.AddDays(-2));

      (int exitCode, _, string error) = Run("acme", "repo");

      Assert.True(exitCode == 0, error);
      Assert.Equal(["acme.other.222"], Leftovers(), StringComparer.Ordinal);
      Assert.Equal("mine", File.ReadAllText(Path.Combine(sibling, "kept.txt")));
   }



   [Fact]
   public async Task AScriptThatIsEndedByASignal_LeavesNoTemporaryFolder_AndNoClone()
   {
      if(!CanRun())
      {
         return;
      }

      // A stand-in for git that clones and then stays busy for a moment, so that the
      // signal arrives while the script is waiting for it. A shell runs its handler
      // when the command it waits for has ended; the handler ends the script, and
      // ending it removes the temporary folder.
      string wrappers = Path.Combine(_root, "wrappers");
      Directory.CreateDirectory(wrappers);
      File.WriteAllText(
          Path.Combine(wrappers, "git"),
          "#!/bin/sh\n\"$GCLO_TEST_REAL_GIT\" \"$@\"\nrc=$?\ncase \" $* \" in *\" clone \"*) sleep 3;; esac\nexit $rc\n");
      string script = Path.Combine(_root, "script.sh");
      File.WriteAllText(script, WslCloner.CloneScript);

      var start = new ProcessStartInfo(s_tools!.Value.Sh) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
      start.ArgumentList.Add("-c");
      start.ArgumentList.Add(
          "sh \"$GCLO_TEST_SCRIPT\" acme repo \"$GCLO_TEST_URL\" example.test & pid=$!; sleep 1; kill -TERM $pid; wait $pid; echo \"exit=$?\"");
      start.Environment["HOME"] = _home.Replace('\\', '/');
      start.Environment[WslCloner.TokenVariable] = "not-a-token";
      start.Environment["WSL_DISTRO_NAME"] = "test";
      start.Environment["GCLO_TEST_REAL_GIT"] = s_tools.Value.Git;
      start.Environment["GCLO_TEST_SCRIPT"] = script.Replace('\\', '/');
      start.Environment["GCLO_TEST_URL"] = _origin.Replace('\\', '/');
      start.Environment["PATH"] = PathWith(s_tools.Value.Sh, wrappers);

      using Process process = Process.Start(start)!;
      Task<string> errorText = process.StandardError.ReadToEndAsync();
      string output = await process.StandardOutput.ReadToEndAsync();
      string error = await errorText;
      await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));

      Assert.Contains("exit=143", output, StringComparison.Ordinal); // ended by the signal, not by finishing
      Assert.False(Directory.Exists(Destination("acme", "repo")), error);
      Assert.Empty(Leftovers());
   }
}
