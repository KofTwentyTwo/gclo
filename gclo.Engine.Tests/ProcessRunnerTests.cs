/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Globalization;


namespace gclo.Engine.Tests;


/// <summary>
/// <see cref="ProcessRunner"/> against real, local child processes (the Windows
/// command interpreter and PowerShell; nothing leaves the machine). What matters is
/// what the caller gets back and that no child is left behind (#151).
/// </summary>
public sealed class ProcessRunnerTests : IDisposable
{
   private readonly string _root =
       Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

   private readonly ProcessRunner _runner = new();



   public void Dispose() => GitTestHelpers.TryDeleteDirectory(_root);



   [Fact]
   public async Task Run_ReturnsTheExitCodeAndWhatTheChildPrinted()
   {
      if(!OperatingSystem.IsWindows())
      {
         return; // drives cmd.exe
      }

      ProcessResult result = await _runner.RunAsync(
          "cmd.exe", ["/d", "/c", "echo out& echo err 1>&2& exit 3"], environment: null, standardInput: null, CancellationToken.None);

      Assert.Equal(3, result.ExitCode);
      Assert.Equal("out", result.StandardOutput.Trim());
      Assert.Equal("err", result.StandardError.Trim());
   }



   [Fact]
   public async Task Run_PassesStandardInputAndTheEnvironmentToTheChild()
   {
      if(!OperatingSystem.IsWindows())
      {
         return; // drives cmd.exe
      }

      ProcessResult result = await _runner.RunAsync(
          "cmd.exe",
          ["/d", "/c", "echo %GCLO_TEST_VALUE%& more"],
          new Dictionary<string, string>(StringComparer.Ordinal) { ["GCLO_TEST_VALUE"] = "from-the-environment" },
          "from standard input",
          CancellationToken.None);

      Assert.Equal(0, result.ExitCode);
      Assert.Contains("from-the-environment", result.StandardOutput, StringComparison.Ordinal);
      Assert.Contains("from standard input", result.StandardOutput, StringComparison.Ordinal);
   }



   [Fact]
   public async Task Run_AChildThatExitsWithoutReadingItsInput_AnswersWithItsExitCodeAndErrorOutput()
   {
      if(!OperatingSystem.IsWindows())
      {
         return; // drives cmd.exe
      }
      // Far more than a pipe holds, so the write is still under way when the child is gone.
      string input = new('x', 4 * 1024 * 1024);

      ProcessResult result = await _runner.RunAsync(
          "cmd.exe", ["/d", "/c", "echo not today 1>&2& exit 7"], environment: null, input, CancellationToken.None)
          .WaitAsync(TimeSpan.FromSeconds(60));

      Assert.Equal(7, result.ExitCode);
      Assert.Equal("not today", result.StandardError.Trim());
   }



   [Fact]
   public async Task Run_Canceled_EndsTheChild_AndThrowsCancellation()
   {
      if(!OperatingSystem.IsWindows())
      {
         return; // drives powershell.exe
      }
      Directory.CreateDirectory(_root);
      string idFile = Path.Combine(_root, "child.id");
      using var cts = new CancellationTokenSource();

      // The child says who it is, then sleeps far longer than the test runs.
      Task<ProcessResult> run = _runner.RunAsync(
          "powershell.exe",
          ["-NoProfile", "-NonInteractive", "-Command", "Set-Content -LiteralPath $env:GCLO_TEST_ID_FILE -Value $PID; Start-Sleep -Seconds 600"],
          new Dictionary<string, string>(StringComparer.Ordinal) { ["GCLO_TEST_ID_FILE"] = idFile },
          standardInput: null,
          cts.Token);
      int childId;
      try
      {
         childId = await ReadChildIdAsync(idFile, run);
      }
      finally
      {
         cts.Cancel(); // also when the child never said who it is: it must not outlive the test
      }

      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(60)));
      Assert.True(await IsGoneAsync(childId), $"child process {childId} is still running after cancellation");
   }



   private static async Task<int> ReadChildIdAsync(string idFile, Task<ProcessResult> run)
   {
      var clock = Stopwatch.StartNew();
      while(clock.Elapsed < TimeSpan.FromSeconds(60))
      {
         Assert.False(run.IsCompleted, "the child ended before it said who it is");
         try
         {
            if(File.Exists(idFile) && int.TryParse(File.ReadAllText(idFile).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
               return id;
            }
         }
         catch(IOException)
         {
            // Still being written.
         }
         await Task.Delay(50);
      }
      throw new TimeoutException("the child never said who it is");
   }



   private static async Task<bool> IsGoneAsync(int processId)
   {
      var clock = Stopwatch.StartNew();
      while(clock.Elapsed < TimeSpan.FromSeconds(30))
      {
         try
         {
            using var process = Process.GetProcessById(processId);
            if(process.HasExited)
            {
               return true;
            }
         }
         catch(ArgumentException)
         {
            return true; // no such process any more
         }
         await Task.Delay(50);
      }
      return false;
   }
}
