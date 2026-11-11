/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using gclo.Engine;


namespace gclo.ViewModels;


/// <summary>
/// Says in the activity log when the window stopped responding, for how long, and
/// what was running (#167). It asks the window's thread to answer a few times a
/// second and measures how long each answer takes: a late answer is time in which a
/// click or a repaint waited just as long. A window that stays unresponsive is
/// reported once while it still is, so a hang that never ends leaves a line too.
/// After one report, further stalls in the next <see cref="QuietPeriod"/> are counted
/// and reported together, so a busy run cannot fill the log.
/// </summary>
public sealed class ResponsivenessMonitor : IDisposable
{
   /// <summary>How often the window's thread is asked to answer.</summary>
   public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(100);

   /// <summary>An answer that takes at least this long is reported.</summary>
   public static readonly TimeSpan DefaultThreshold = TimeSpan.FromMilliseconds(250);

   /// <summary>A window that has not answered for this long is reported while it still has not.</summary>
   public static readonly TimeSpan StillWaitingAfter = TimeSpan.FromSeconds(5);

   /// <summary>After a report, how long further stalls are collected before they are reported together.</summary>
   public static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(10);

   /// <summary>
   /// When this much time lies between two looks, the whole process was not running
   /// (the machine slept, a debugger held it): the question in flight says nothing
   /// about the window and is dropped.
   /// </summary>
   public static readonly TimeSpan PauseAfter = TimeSpan.FromSeconds(2);

   private readonly Func<Action, bool> _post;

   private readonly IActivityLog _log;

   private readonly TimeProvider _time;

   private readonly TimeSpan _threshold;

   private readonly ITimer _timer;

   private readonly Lock _gate = new();

   /// <summary>When the question in flight was asked.</summary>
   private long _askedAt;

   /// <summary>When it was answered; valid once <see cref="_answered"/> is true.</summary>
   private long _answeredAt;

   private volatile bool _answered;

   /// <summary>True while a question is in flight.</summary>
   private bool _asked;

   private bool _saidStillWaiting;

   private bool _stopped;

   /// <summary>When the last stall got its own line; stalls are collected from then on. Null when the next one gets a line.</summary>
   private long? _quietSince;

   private int _collected;

   private TimeSpan _collectedTotal;

   private TimeSpan _collectedLongest;

   /// <summary>What was running at the latest collected stall.</summary>
   private string? _collectedActivity;

   /// <summary>When <see cref="Check"/> last ran; null before the first look.</summary>
   private long? _lookedAt;

   private volatile string? _activity;



   /// <summary>Starts watching.</summary>
   /// <param name="post">
   /// Queues an action on the window's thread; false when that thread no longer takes
   /// work (the window is closing), which ends the watch.
   /// </param>
   /// <param name="log">Where stalls are reported.</param>
   /// <param name="timeProvider">Clock and timer source; the system's by default.</param>
   /// <param name="interval">How often to ask; <see cref="DefaultInterval"/> by default.</param>
   /// <param name="threshold">The shortest stall worth a line; <see cref="DefaultThreshold"/> by default.</param>
   public ResponsivenessMonitor(
       Func<Action, bool> post,
       IActivityLog log,
       TimeProvider? timeProvider = null,
       TimeSpan? interval = null,
       TimeSpan? threshold = null)
   {
      ArgumentNullException.ThrowIfNull(post);
      ArgumentNullException.ThrowIfNull(log);
      _post = post;
      _log = log;
      _time = timeProvider ?? TimeProvider.System;
      _threshold = threshold ?? DefaultThreshold;
      TimeSpan period = interval ?? DefaultInterval;
      _timer = _time.CreateTimer(_ => Check(), null, period, period);
   }



   /// <summary>
   /// What the app is doing, in words that follow "while" (for example "syncing
   /// 'Engineering'"); named in each report. Null or empty when nothing is running.
   /// </summary>
   public string? Activity
   {
      get => _activity;
      set => _activity = value;
   }



   /// <summary>Stops watching; stalls collected but not yet reported are reported now.</summary>
   public void Dispose()
   {
      lock(_gate)
      {
         if(_stopped)
         {
            return;
         }
         _stopped = true;
         ReportCollected();
      }
      _timer.Dispose();
   }



   /// <summary>
   /// One look, on the timer's thread: was the last question answered, how late, and
   /// if so ask the next one.
   /// </summary>
   internal void Check()
   {
      lock(_gate)
      {
         if(_stopped)
         {
            return;
         }

         if(_quietSince is long since && _time.GetElapsedTime(since) >= QuietPeriod)
         {
            ReportCollected();
            _quietSince = null;
         }

         bool wasPaused = _lookedAt is long last && _time.GetElapsedTime(last) >= PauseAfter;
         _lookedAt = _time.GetTimestamp();
         if(wasPaused)
         {
            _asked = false;
         }

         if(_asked)
         {
            if(!_answered)
            {
               TimeSpan soFar = _time.GetElapsedTime(_askedAt);
               if(soFar >= StillWaitingAfter && !_saidStillWaiting)
               {
                  _saidStillWaiting = true;
                  _log.Error($"The window has not responded for {Milliseconds(soFar)} ms{WhileWhat(_activity)} and still does not.");
               }
               return;
            }

            TimeSpan waited = _time.GetElapsedTime(_askedAt, _answeredAt);
            if(waited >= _threshold)
            {
               Note(waited);
            }
         }

         Ask();
      }
   }



   private void Ask()
   {
      _askedAt = _time.GetTimestamp();
      _answered = false;
      _saidStillWaiting = false;
      _asked = _post(Answer);
      if(!_asked)
      {
         // The window's thread takes no more work: there is nothing left to watch.
         _stopped = true;
         ReportCollected();
      }
   }



   /// <summary>Runs on the window's thread: nothing but a timestamp, no file is touched there.</summary>
   private void Answer()
   {
      _answeredAt = _time.GetTimestamp();
      _answered = true;
   }



   /// <summary>The first stall after a quiet time gets its own line; the ones that follow closely are collected.</summary>
   private void Note(TimeSpan waited)
   {
      if(_quietSince is null)
      {
         _log.Info($"The window did not respond for {Milliseconds(waited)} ms{WhileWhat(_activity)}.");
         _quietSince = _time.GetTimestamp();
         return;
      }

      _collected++;
      _collectedActivity = _activity;
      _collectedTotal += waited;
      if(waited > _collectedLongest)
      {
         _collectedLongest = waited;
      }
   }



   private void ReportCollected()
   {
      if(_collected == 0)
      {
         return;
      }

      string times = _collected == 1 ? "once more" : $"{_collected.ToString(CultureInfo.InvariantCulture)} more times";
      _log.Info(
          $"Since then the window did not respond {times}{WhileWhat(_collectedActivity)}: {Milliseconds(_collectedTotal)} ms in total, "
          + $"the longest {Milliseconds(_collectedLongest)} ms.");
      _collected = 0;
      _collectedTotal = TimeSpan.Zero;
      _collectedLongest = TimeSpan.Zero;
   }



   private static string WhileWhat(string? activity) => activity is { Length: > 0 } ? $" while {activity}" : "";



   private static string Milliseconds(TimeSpan span)
       => ((long)span.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
}
