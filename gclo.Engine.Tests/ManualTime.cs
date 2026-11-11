/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine.Tests;


/// <summary>
/// A clock that moves only when a test moves it, and timers that fire only when a
/// test fires them: what happens "after 100 ms" is then a step in the test, not a
/// wait that a slow machine can stretch.
/// </summary>
internal sealed class ManualTime : TimeProvider
{
   private readonly List<ManualTimer> _timers = [];

   private long _now;



   /// <summary>Every timer created from this clock, in creation order.</summary>
   public IReadOnlyList<ManualTimer> Timers
   {
      get
      {
         lock(_timers)
         {
            return [.. _timers];
         }
      }
   }

   public override long TimestampFrequency => TimeSpan.TicksPerSecond;



   public override long GetTimestamp() => Interlocked.Read(ref _now);



   /// <summary>Moves the clock forward; fires nothing.</summary>
   public void Advance(TimeSpan by) => Interlocked.Add(ref _now, by.Ticks);



   public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
   {
      var timer = new ManualTimer(callback, state, dueTime, period);
      lock(_timers)
      {
         _timers.Add(timer);
      }
      return timer;
   }



   /// <summary>One timer: armed or not, fired by the test.</summary>
   internal sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
   {
      /// <summary>When it is next due, counted from the last arming; infinite when it is not armed.</summary>
      public TimeSpan DueTime { get; private set; } = dueTime;

      /// <summary>Its period; infinite for a timer that fires once per arming.</summary>
      public TimeSpan Period { get; private set; } = period;

      /// <summary>How many times it was armed again after it was created.</summary>
      public int Changes { get; private set; }

      public bool IsDisposed { get; private set; }

      public bool IsArmed => !IsDisposed && DueTime != Timeout.InfiniteTimeSpan;



      public bool Change(TimeSpan dueTime, TimeSpan period)
      {
         if(IsDisposed)
         {
            return false;
         }
         DueTime = dueTime;
         Period = period;
         Changes++;
         return true;
      }



      /// <summary>Runs the callback as the timer would when due; a timer without a period is then no longer armed.</summary>
      public void Fire()
      {
         Assert.True(IsArmed, "the timer is not armed");
         if(Period == Timeout.InfiniteTimeSpan)
         {
            DueTime = Timeout.InfiniteTimeSpan;
         }
         callback(state);
      }



      public void Dispose() => IsDisposed = true;



      public ValueTask DisposeAsync()
      {
         Dispose();
         return ValueTask.CompletedTask;
      }
   }
}
