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
   /// <summary>"yyyy-MM-dd HH:mm:ss.fff +hh:mm [LEVEL] " prefix every entry line must carry.</summary>
   private static readonly Regex s_linePattern = new(
       @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2} \[(INFO|ERROR)\] ", RegexOptions.Compiled | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

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
      string forged = "repo failed: bad\n2026-01-01 00:00:00.000 +00:00 [INFO] all repositories synced" + (char)0x1B + "[2J";

      log.Error(forged);

      string line = Assert.Single(File.ReadAllLines(log.CurrentLogFilePath));
      Assert.Matches(s_linePattern, line);
      Assert.Contains("[ERROR] repo failed: bad", line);
      Assert.Contains(Visible(0x0A) + "2026-01-01 00:00:00.000 +00:00 [INFO] all repositories synced", line);
      Assert.EndsWith(Visible(0x1B) + "[2J", line, StringComparison.Ordinal);
      Assert.DoesNotContain((char)0x1B, line);
   }



   [Fact]
   public void Exception_WhoseTextImitatesAnEntry_CannotStartALineInTheFirstColumn()
   {
      var log = new FileActivityLog(_root);
      var exception = new InvalidOperationException(
          "bad path 'a\n2026-01-01 00:00:00.000 +00:00 [INFO] forged entry' in tree" + (char)0x1B + "[0m");

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
      Assert.Contains(lines, l => l.Contains("2026-01-01 00:00:00.000 +00:00 [INFO] forged entry", StringComparison.Ordinal));
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



   // ---------------------------------------------------------------- the clock (#151)

   /// <summary>A clock the test sets; it counts how often it is read.</summary>
   private sealed class SetClock(DateTimeOffset now) : TimeProvider
   {
      public DateTimeOffset Now { get; set; } = now;

      public int Reads { get; private set; }

      /// <summary>What every reading adds to the clock afterwards.</summary>
      public TimeSpan StepPerRead { get; set; }

      public override TimeZoneInfo LocalTimeZone
          => TimeZoneInfo.CreateCustomTimeZone("test", Now.Offset, "test", "test");



      public override DateTimeOffset GetUtcNow()
      {
         Reads++;
         DateTimeOffset reading = Now;
         Now += StepPerRead;
         return reading.ToUniversalTime();
      }
   }



   [Fact]
   public void Entry_CarriesItsOffsetFromUtc_AndGoesToTheFileOfItsDay()
   {
      var clock = new SetClock(new DateTimeOffset(2026, 3, 4, 5, 6, 7, 89, TimeSpan.FromHours(-6)));
      var log = new FileActivityLog(_root, clock, TimeSpan.Zero);

      log.Info("hello");

      string file = Path.Combine(_root, "gclo-2026-03-04.log");
      Assert.Equal("2026-03-04 05:06:07.089 -06:00 [INFO] hello", Assert.Single(File.ReadAllLines(file)));
      Assert.Equal(file, log.CurrentLogFilePath);
   }



   [Fact]
   public void Entry_ReadsTheClockOnce_SoItsTimestampAndItsFileAgreeAtMidnight()
   {
      // Every reading moves this clock on by a millisecond, starting at the last one of a day.
      var clock = new SetClock(new DateTimeOffset(2026, 3, 4, 23, 59, 59, 999, TimeSpan.FromHours(2)))
      {
         StepPerRead = TimeSpan.FromMilliseconds(1),
      };
      var log = new FileActivityLog(_root, clock, TimeSpan.Zero);

      log.Info("last of the day");
      log.Info("first of the next");

      Assert.Equal(2, clock.Reads);
      Assert.Equal(
          "2026-03-04 23:59:59.999 +02:00 [INFO] last of the day",
          Assert.Single(File.ReadAllLines(Path.Combine(_root, "gclo-2026-03-04.log"))));
      Assert.Equal(
          "2026-03-05 00:00:00.000 +02:00 [INFO] first of the next",
          Assert.Single(File.ReadAllLines(Path.Combine(_root, "gclo-2026-03-05.log"))));
   }



   // ---------------------------------------------------------------- old files (#151)

   private string Touch(string name)
   {
      Directory.CreateDirectory(_root);
      string path = Path.Combine(_root, name);
      File.WriteAllText(path, "old");
      return path;
   }



   [Fact]
   public void Write_RemovesDailyFilesOlderThanTheRetention_AndNothingElse()
   {
      var clock = new SetClock(new DateTimeOffset(2026, 3, 31, 12, 0, 0, TimeSpan.Zero));
      var log = new FileActivityLog(_root, clock, TimeSpan.Zero);
      string tooOld = Touch("gclo-2026-03-01.log");
      string muchTooOld = Touch("gclo-2019-12-31.log");
      string[] kept =
      [
          Touch("gclo-2026-03-02.log"), // the thirtieth day, today included
          Touch("gclo-notes.log"),
          Touch("gclo-2020-13-45.log"),
          Touch("other-2020-01-01.log"),
          Touch("gclo-2020-01-01.log.bak"),
          Touch("gclo-2020-01-01-copy.log"),
      ];

      log.Info("today");

      Assert.Equal(30, FileActivityLog.RetentionDays);
      Assert.False(File.Exists(tooOld));
      Assert.False(File.Exists(muchTooOld));
      Assert.All(kept, file => Assert.True(File.Exists(file), file));
      Assert.Single(File.ReadAllLines(Path.Combine(_root, "gclo-2026-03-31.log")));
   }



   [Fact]
   public void Write_LooksForOldFilesOnceADay()
   {
      var clock = new SetClock(new DateTimeOffset(2026, 3, 31, 12, 0, 0, TimeSpan.Zero));
      var log = new FileActivityLog(_root, clock, TimeSpan.Zero);
      log.Info("first of the day");
      string old = Touch("gclo-2020-01-01.log");

      log.Info("later the same day");
      Assert.True(File.Exists(old));

      clock.Now = clock.Now.AddDays(1);
      log.Info("the next day");
      Assert.False(File.Exists(old));
   }



   [Fact]
   public void Write_AnOldFileThatCannotBeRemoved_IsLeft_AndTheRestOfTheWorkIsDone()
   {
      if(!OperatingSystem.IsWindows())
      {
         return; // elsewhere a file that is open can still be removed
      }
      var clock = new SetClock(new DateTimeOffset(2026, 3, 31, 12, 0, 0, TimeSpan.Zero));
      var log = new FileActivityLog(_root, clock, TimeSpan.Zero);
      string open = Touch("gclo-2020-01-01.log");
      string free = Touch("gclo-2020-01-02.log");

      using(new FileStream(open, FileMode.Open, FileAccess.Read, FileShare.Read))
      {
         log.Info("today");
      }

      Assert.True(File.Exists(open));
      Assert.False(File.Exists(free));
      Assert.Single(File.ReadAllLines(log.CurrentLogFilePath));
   }



   // ---------------------------------------------------------------- two writers of one file (#151)

   /// <summary>A clock that stands still, so a test's file name does not depend on when it runs.</summary>
   private static SetClock Noon() => new(new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.Zero));



   /// <summary>Holds the file the way another process in the middle of an entry does.</summary>
   private static FileStream HoldAsAnotherWriter(string path)
       => new(path, FileMode.Append, FileAccess.Write, FileShare.Read);



   [Fact]
   public async Task Write_WhileAnotherWriterHoldsTheFile_WaitsAndThenGetsThrough()
   {
      if(!OperatingSystem.IsWindows())
      {
         return; // file sharing is what keeps the other writer out
      }
      var log = new FileActivityLog(_root, Noon(), TimeSpan.FromSeconds(60));
      log.Info("first");
      Task write;

      using(HoldAsAnotherWriter(log.CurrentLogFilePath))
      {
         write = Task.Run(() => log.Info("second"));
         Task finished = await Task.WhenAny(write, Task.Delay(TimeSpan.FromMilliseconds(200)));
         Assert.NotSame(write, finished); // still waiting for the file
      }
      await write.WaitAsync(TimeSpan.FromSeconds(60));

      string[] lines = File.ReadAllLines(log.CurrentLogFilePath);
      Assert.Equal(2, lines.Length);
      Assert.EndsWith("first", lines[0]);
      Assert.EndsWith("second", lines[1]);
   }



   [Fact]
   public void Write_WhenTheFileStaysHeld_GivesUpAfterItsPatience_AndDoesNotWaitAgainBeforeTheCoolDownIsOver()
   {
      if(!OperatingSystem.IsWindows())
      {
         return; // file sharing is what keeps the other writer out
      }
      int waits = 0;
      var log = new FileActivityLog(_root, Noon(), TimeSpan.FromMilliseconds(300), TimeSpan.FromHours(1))
      {
         Pause = pause =>
         {
            waits++;
            Thread.Sleep(pause);
         },
      };
      log.Info("first");
      string path = log.CurrentLogFilePath;

      using(HoldAsAnotherWriter(path))
      {
         log.Info("lost after waiting");
         int waitsOfTheFirst = waits;
         log.Info("lost without waiting");

         Assert.True(waitsOfTheFirst > 0, "the first write did not wait for the file");
         Assert.Equal(waitsOfTheFirst, waits);
      }

      // A write that gets through does not bring the waiting back: a file that is
      // held again and again must not stall its writers once per release.
      log.Info("back");
      using(HoldAsAnotherWriter(path))
      {
         int before = waits;
         log.Info("lost without waiting, again");
         Assert.Equal(before, waits);
      }

      string[] lines = File.ReadAllLines(path);
      Assert.Equal(2, lines.Length);
      Assert.EndsWith("first", lines[0]);
      Assert.EndsWith("back", lines[1]);
   }



   [Fact]
   public void Write_OnceTheCoolDownIsOver_WaitsForAHeldFileAgain()
   {
      if(!OperatingSystem.IsWindows())
      {
         return; // file sharing is what keeps the other writer out
      }
      int waits = 0;
      var coolDown = TimeSpan.FromMilliseconds(100);
      var log = new FileActivityLog(_root, Noon(), TimeSpan.FromMilliseconds(100), coolDown)
      {
         Pause = pause =>
         {
            waits++;
            Thread.Sleep(pause);
         },
      };
      log.Info("first");

      using(HoldAsAnotherWriter(log.CurrentLogFilePath))
      {
         log.Info("lost after waiting");
         int waitsOfTheFirst = waits;
         Thread.Sleep(coolDown + TimeSpan.FromMilliseconds(100));
         log.Info("lost after waiting again");

         Assert.True(waitsOfTheFirst > 0, "the first write did not wait for the file");
         Assert.True(waits > waitsOfTheFirst, "the write after the cool-down did not wait for the file");
      }
   }



   [Fact]
   public void TwoWritersOfOneFile_LoseNoEntry_AndWriteOnlyWholeLines()
   {
      if(!OperatingSystem.IsWindows())
      {
         return; // file sharing is what keeps the other writer out
      }
      // Two logs over one folder stand for the desktop app and the CLI.
      var app = new FileActivityLog(_root, Noon(), TimeSpan.FromSeconds(60));
      var cli = new FileActivityLog(_root, Noon(), TimeSpan.FromSeconds(60));
      const int PerWriter = 150;

      Parallel.Invoke(
          () =>
          {
             for(int i = 0; i < PerWriter; i++)
             {
                app.Info($"app entry {i}");
             }
          },
          () =>
          {
             for(int i = 0; i < PerWriter; i++)
             {
                cli.Info($"cli entry {i}");
             }
          });

      string[] lines = File.ReadAllLines(app.CurrentLogFilePath);
      Assert.Equal(2 * PerWriter, lines.Length);
      Assert.All(lines, line => Assert.Matches(s_linePattern, line));
      Assert.Equal(PerWriter, lines.Count(l => l.Contains(" app entry ", StringComparison.Ordinal)));
      Assert.Equal(PerWriter, lines.Count(l => l.Contains(" cli entry ", StringComparison.Ordinal)));
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
