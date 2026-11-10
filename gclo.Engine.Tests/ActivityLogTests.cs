/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Text.RegularExpressions;


namespace gclo.Engine.Tests;


/// <summary>
/// Tests for <see cref="FileActivityLog"/> (line format, daily file naming, lazy
/// directory creation, swallowing IO failures, thread safety) and
/// <see cref="NullActivityLog"/> (pure no-op).
/// </summary>
public sealed class ActivityLogTests : IDisposable
{
   /// <summary>"yyyy-MM-dd HH:mm:ss.fff [LEVEL] " prefix every entry line must carry.</summary>
   private static readonly Regex s_linePattern = new(
       @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[(INFO|ERROR)\] ", RegexOptions.Compiled | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

   private readonly string _root =
       Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));



   public void Dispose()
   {
      try
      {
         if(Directory.Exists(_root))
         {
            Directory.Delete(_root, recursive: true);
         }
      }
      catch
      {
         // Best effort; stray empty temp dirs are harmless.
      }
   }



   /// <summary>The visible form of one UTF-16 code unit: a backslash, "u", four upper-case hex digits.</summary>
   private static string Visible(int unit)
       => "\\" + "u" + unit.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);



   // ---------------------------------------------------------------- line format

   [Fact]
   public void Info_WritesOneTimestampedLine_WithInfoTag()
   {
      var log = new FileActivityLog(_root);

      log.Info("hello world");

      string line = Assert.Single(File.ReadAllLines(log.CurrentLogFilePath));
      Assert.Matches(s_linePattern, line);
      Assert.Contains("[INFO] ", line);
      Assert.EndsWith("hello world", line);
   }



   [Fact]
   public void Error_WithoutException_WritesOneLineWithErrorTag()
   {
      var log = new FileActivityLog(_root);

      log.Error("something failed");

      string line = Assert.Single(File.ReadAllLines(log.CurrentLogFilePath));
      Assert.Matches(s_linePattern, line);
      Assert.Contains("[ERROR] ", line);
      Assert.EndsWith("something failed", line);
   }



   [Fact]
   public void Message_WithLineBreaksAndEscapeCharacters_IsStillOneEntryOnOneLine()
   {
      var log = new FileActivityLog(_root);
      // What a repository can put into a message: a file path with a newline that
      // imitates the start of another entry, and a terminal escape sequence.
      string forged = "repo failed: bad\n2026-01-01 00:00:00.000 [INFO] all repositories synced" + (char)0x1B + "[2J";

      log.Error(forged);

      string line = Assert.Single(File.ReadAllLines(log.CurrentLogFilePath));
      Assert.Matches(s_linePattern, line);
      Assert.Contains("[ERROR] repo failed: bad", line);
      Assert.Contains(Visible(0x0A) + "2026-01-01 00:00:00.000 [INFO] all repositories synced", line);
      Assert.EndsWith(Visible(0x1B) + "[2J", line, StringComparison.Ordinal);
      Assert.DoesNotContain((char)0x1B, line);
   }



   [Fact]
   public void Exception_WhoseTextImitatesAnEntry_CannotStartALineInTheFirstColumn()
   {
      var log = new FileActivityLog(_root);
      var exception = new InvalidOperationException(
          "bad path 'a\n2026-01-01 00:00:00.000 [INFO] forged entry' in tree" + (char)0x1B + "[0m");

      log.Error("sync blew up", exception);
      log.Info("next entry");

      string[] lines = File.ReadAllLines(log.CurrentLogFilePath);
      // Exactly two lines look like entries: the real ones.
      Assert.Equal(2, lines.Count(l => s_linePattern.IsMatch(l)));
      Assert.Matches(s_linePattern, lines[0]);
      Assert.Matches(s_linePattern, lines[^1]);
      Assert.All(lines[1..^1], l => Assert.StartsWith("   ", l, StringComparison.Ordinal));
      // The indent is what keeps a continuation line from matching an entry's start,
      // for this pattern and for the Log window's filter alike.
      Assert.All(lines[1..^1], l => Assert.DoesNotMatch(s_linePattern, l));
      Assert.Contains(lines, l => l.Contains("2026-01-01 00:00:00.000 [INFO] forged entry", StringComparison.Ordinal));
      Assert.DoesNotContain(lines, l => l.Contains((char)0x1B));
   }



   [Fact]
   public void Error_WithException_AppendsExceptionTextOnFollowingLines()
   {
      var log = new FileActivityLog(_root);

      log.Error("sync blew up", new InvalidOperationException("kaboom"));

      string[] lines = File.ReadAllLines(log.CurrentLogFilePath);
      Assert.True(lines.Length >= 2, "exception text belongs on lines after the message");
      Assert.Matches(s_linePattern, lines[0]);
      Assert.EndsWith("sync blew up", lines[0]);
      Assert.Contains("InvalidOperationException", lines[1]);
      Assert.Contains("kaboom", lines[1]);
   }



   [Fact]
   public void Writes_Accumulate_InTheSameFile()
   {
      var log = new FileActivityLog(_root);

      log.Info("first");
      log.Error("second");
      log.Info("third");

      string[] lines = File.ReadAllLines(log.CurrentLogFilePath);
      Assert.Equal(3, lines.Length);
      Assert.All(lines, line => Assert.Matches(s_linePattern, line));
      Assert.EndsWith("first", lines[0]);
      Assert.EndsWith("second", lines[1]);
      Assert.EndsWith("third", lines[2]);
   }



   // ---------------------------------------------------------------- paths

   [Fact]
   public void CurrentLogFilePath_IsTodaysDailyFile_UnderLogDirectory()
   {
      var log = new FileActivityLog(_root);

      string expected = Path.Combine(
          _root,
          "gclo-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
      Assert.Equal(_root, log.LogDirectory);
      Assert.Equal(expected, log.CurrentLogFilePath);
   }



   [Fact]
   public void DefaultDirectory_IsLocalAppDataKofTwentyTwoGcloLogs()
   {
      var log = new FileActivityLog();

      string expected = Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
          "KofTwentyTwo", "gclo", "logs");
      Assert.Equal(expected, log.LogDirectory);
   }



   [Fact]
   public void DefaultDirectory_IsNotInsideTheVelopackInstallRoot()
   {
      // Setup.exe deletes %LOCALAPPDATA%\gclo (the install root) on reinstall (#91).
      string installRoot = Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "gclo");

      Assert.False(
          new FileActivityLog().LogDirectory.StartsWith(installRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
   }



   [Fact]
   public void Directory_IsCreatedLazily_OnFirstWriteNotInConstructor()
   {
      string directory = Path.Combine(_root, "lazy");

      var log = new FileActivityLog(directory);
      Assert.False(Directory.Exists(directory), "constructor must not create the directory");

      log.Info("first write");

      Assert.True(Directory.Exists(directory));
      Assert.True(File.Exists(log.CurrentLogFilePath));
   }



   // ---------------------------------------------------------------- never throws

   [Fact]
   public void Write_WhenDirectoryIsUnwritable_NeverThrows()
   {
      // A file sitting where the log directory should go makes CreateDirectory
      // throw; the log must swallow that on every call.
      Directory.CreateDirectory(_root);
      string fileAsDirectory = Path.Combine(_root, "not-a-directory");
      File.WriteAllText(fileAsDirectory, "blocks directory creation");

      var log = new FileActivityLog(fileAsDirectory);

      log.Info("goes nowhere");
      log.Error("still nowhere", new InvalidOperationException("boom"));

      // The properties still answer; only the writes are lost.
      Assert.Equal(fileAsDirectory, log.LogDirectory);
      Assert.StartsWith(fileAsDirectory, log.CurrentLogFilePath);
   }



   // ---------------------------------------------------------------- thread safety

   [Fact]
   public void ParallelWrites_ProduceOnlyWholeWellFormedLines()
   {
      var log = new FileActivityLog(_root);
      const int Writers = 4;
      const int PerWriter = 25;

      Parallel.For(0, Writers, writer =>
      {
         for(int i = 0; i < PerWriter; i++)
         {
            log.Info($"writer {writer} message {i}");
         }
      });

      string[] lines = File.ReadAllLines(log.CurrentLogFilePath);
      Assert.Equal(Writers * PerWriter, lines.Length);
      Assert.All(lines, line => Assert.Matches(s_linePattern, line));
   }



   // ---------------------------------------------------------------- NullActivityLog

   [Fact]
   public void NullActivityLog_NoOps_AndReturnsEmptyStrings()
   {
      var log = new NullActivityLog();

      log.Info("ignored");
      log.Error("ignored too", new InvalidOperationException("ignored as well"));

      Assert.Equal("", log.LogDirectory);
      Assert.Equal("", log.CurrentLogFilePath);
   }
}
