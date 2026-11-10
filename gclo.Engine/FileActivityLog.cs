/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Globalization;
using System.Text;


namespace gclo.Engine;


/// <summary>
/// An <see cref="IActivityLog"/> that appends timestamped lines to a daily file,
/// gclo-yyyy-MM-dd.log. Writes are serialized by a lock and each one opens,
/// appends, and closes the file, so every entry reaches disk even if the process
/// dies right after. The desktop app and the CLI can write to the same file: a write
/// that finds the file held by the other waits briefly and tries again, and one that
/// gave up does not make later writes wait until a cool-down is over. Files
/// older than <see cref="RetentionDays"/> days are removed. Logging must never
/// take the application down: all IO failures (unwritable directory, locked file,
/// full disk) are swallowed. The directory is created lazily on the first write,
/// never in the constructor.
/// </summary>
public sealed class FileActivityLog : IActivityLog
{
   /// <summary>
   /// How many days of log files are kept, today included. A daily file older than
   /// that is removed the first time an entry is written on a given day.
   /// </summary>
   public const int RetentionDays = 30;

   /// <summary>What every line of an exception's text starts with, under its entry.</summary>
   private const string ContinuationIndent = "   ";

   private const string FilePrefix = "gclo-";

   private const string FileDateFormat = "yyyy-MM-dd";

   /// <summary>How long one write waits for a file another process is writing to.</summary>
   private static readonly TimeSpan s_defaultContentionBudget = TimeSpan.FromMilliseconds(200);

   /// <summary>How long after a write gave up no write waits for the file again.</summary>
   private static readonly TimeSpan s_defaultCoolDown = TimeSpan.FromSeconds(30);

   private static readonly TimeSpan s_retryPause = TimeSpan.FromMilliseconds(1);

   private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

   private readonly Lock _gate = new();

   private readonly TimeProvider _time;

   private readonly TimeSpan _contentionBudget;

   private readonly TimeSpan _coolDown;

   /// <summary>The day old files were last removed for; the work is done once a day.</summary>
   private DateOnly _prunedFor;

   /// <summary>
   /// When a write last gave up on a file it could not open (a stopwatch reading;
   /// zero when none has). Until the cool-down after it is over, each write tries
   /// once and does not wait: a file that stays locked, or is locked again and again,
   /// costs the operations being logged one wait per cool-down, not one per entry.
   /// </summary>
   private long _gaveUpAt;



   /// <summary>Creates a log that writes under <paramref name="directory"/>.</summary>
   /// <param name="directory">
   /// Folder for the log files; defaults to %LOCALAPPDATA%\KofTwentyTwo\gclo\logs
   /// when null or blank (the same data root as the app; never the Velopack
   /// install root %LOCALAPPDATA%\gclo, which Setup.exe deletes, #91).
   /// </param>
   public FileActivityLog(string? directory = null)
       : this(directory, TimeProvider.System, s_defaultContentionBudget)
   {
   }



   /// <summary>Creates a log with its own clock, patience, and cool-down, for tests.</summary>
   internal FileActivityLog(string? directory, TimeProvider time, TimeSpan contentionBudget, TimeSpan? coolDown = null)
   {
      LogDirectory = string.IsNullOrWhiteSpace(directory)
          ? Path.Combine(
              Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
              "KofTwentyTwo", "gclo", "logs")
          : directory;
      _time = time;
      _contentionBudget = contentionBudget;
      _coolDown = coolDown ?? s_defaultCoolDown;
   }



   /// <summary>How a write waits between two tries; tests count the waits through it.</summary>
   internal Action<TimeSpan> Pause { get; init; } = Thread.Sleep;



   /// <inheritdoc/>
   public string LogDirectory { get; }



   /// <summary>Today's log file; entries roll over to a new file at midnight.</summary>
   public string CurrentLogFilePath => PathFor(_time.GetLocalNow());



   /// <inheritdoc/>
   public void Info(string message) => Write("INFO", message, exception: null);



   /// <inheritdoc/>
   public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);



   private string PathFor(DateTimeOffset moment)
       => Path.Combine(
           LogDirectory,
           FilePrefix + moment.ToString(FileDateFormat, CultureInfo.InvariantCulture) + ".log");



   private void Write(string level, string message, Exception? exception)
   {
      try
      {
         lock(_gate)
         {
            ///////////////////////////////////////////////////////////////////////////
            // The clock is read once per entry, so the timestamp and the file the   //
            // entry goes to always agree, across midnight too. The timestamp        //
            // carries its offset from UTC: a log that spans a daylight-saving       //
            // change, or is read on another machine, still says when each thing     //
            // happened.                                                             //
            ///////////////////////////////////////////////////////////////////////////
            DateTimeOffset now = _time.GetLocalNow();
            Directory.CreateDirectory(LogDirectory);

            string timestamp = now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
            ///////////////////////////////////////////////////////////////////////////
            // An entry is one line that starts with its timestamp; readers (the Log //
            // window's error filter among them) rely on that. Messages carry text   //
            // from repositories, so the message is escaped onto one line, and an    //
            // exception's lines are indented: nothing in them can start a line that //
            // looks like another entry (#143).                                      //
            ///////////////////////////////////////////////////////////////////////////
            string entry = $"{timestamp} [{level}] {DisplayText.Escape(message)}";
            if(exception is not null)
            {
               entry += Environment.NewLine + DisplayText.EscapeLines(exception.ToString(), ContinuationIndent);
            }

            Append(PathFor(now), entry + Environment.NewLine);
            RemoveOldFiles(DateOnly.FromDateTime(now.DateTime));
         }
      }
      catch
      {
         // A logging failure must never take the operation it describes down.
      }
   }



   /// <summary>
   /// Appends <paramref name="text"/> as one write to a file opened for this writer
   /// alone, so entries from two processes never interleave. Another process in the
   /// middle of its own entry holds the file for a moment: the write is tried again
   /// until it gets through or its patience runs out. A write that ran out of
   /// patience is the last one to wait until the cool-down is over.
   /// </summary>
   private void Append(string path, string text)
   {
      byte[] bytes = s_utf8.GetBytes(text);
      bool patient = _gaveUpAt == 0 || Stopwatch.GetElapsedTime(_gaveUpAt) >= _coolDown;
      long started = Stopwatch.GetTimestamp();
      while(true)
      {
         try
         {
            using(var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
               file.Write(bytes);
            }
            return;
         }
         catch(IOException) when(patient && Stopwatch.GetElapsedTime(started) < _contentionBudget)
         {
            Pause(s_retryPause);
         }
         catch(IOException) when(patient)
         {
            _gaveUpAt = Stopwatch.GetTimestamp();
            throw;
         }
      }
   }



   /// <summary>
   /// Removes the daily files older than <see cref="RetentionDays"/> days, once per
   /// day. Only files named exactly like a daily log are touched; one that cannot be
   /// removed (open in a viewer, read-only) is left for another day.
   /// </summary>
   private void RemoveOldFiles(DateOnly today)
   {
      if(_prunedFor == today)
      {
         return;
      }
      _prunedFor = today;

      DateOnly oldestKept = today.AddDays(1 - RetentionDays);
      foreach(string file in Directory.EnumerateFiles(LogDirectory, FilePrefix + "*.log"))
      {
         ReadOnlySpan<char> date = Path.GetFileNameWithoutExtension(file.AsSpan())[FilePrefix.Length..];
         if(!DateOnly.TryParseExact(date, FileDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day)
                || day >= oldestKept)
         {
            continue;
         }

         try
         {
            File.Delete(file);
         }
         catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
         {
            // Left for another day.
         }
      }
   }
}
