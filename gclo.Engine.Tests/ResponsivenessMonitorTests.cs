/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.ViewModels;


namespace gclo.Engine.Tests;


/// <summary>
/// <see cref="ResponsivenessMonitor"/> asks the window's thread to answer and says
/// in the activity log when an answer came late: for how long the window did not
/// respond, and what was running (#167).
/// </summary>
public sealed class ResponsivenessMonitorTests
{
   private readonly ManualTime _time = new();

   private readonly RecordingActivityLog _log = new();

   /// <summary>What the monitor queued on the window's thread and the test has not run yet.</summary>
   private readonly Queue<Action> _asked = new();

   private bool _windowTakesWork = true;



   private ResponsivenessMonitor Create()
       => new(
           answer =>
           {
              if(_windowTakesWork)
              {
                 _asked.Enqueue(answer);
              }
              return _windowTakesWork;
           },
           _log,
           _time);



   /// <summary>The window's thread answers the question in flight after <paramref name="after"/>.</summary>
   private void Answer(TimeSpan after)
   {
      _time.Advance(after);
      _asked.Dequeue()();
   }



   /// <summary>The monitor looks every 100 ms, as its timer would, for the given time; nothing answers.</summary>
   private void Looks(ResponsivenessMonitor monitor, TimeSpan during)
   {
      for(TimeSpan passed = TimeSpan.Zero; passed < during; passed += ResponsivenessMonitor.DefaultInterval)
      {
         _time.Advance(ResponsivenessMonitor.DefaultInterval);
         monitor.Check();
      }
   }



   /// <summary>The window answers every question at once until the quiet period after a report is over.</summary>
   private void QuietPeriodPasses(ResponsivenessMonitor monitor)
   {
      for(TimeSpan passed = TimeSpan.Zero; passed <= ResponsivenessMonitor.QuietPeriod; passed += ResponsivenessMonitor.DefaultInterval)
      {
         Answer(TimeSpan.Zero);
         _time.Advance(ResponsivenessMonitor.DefaultInterval);
         monitor.Check();
      }
   }



   /// <summary>One stall of the given length: asked, answered late, noticed.</summary>
   private void Stall(ResponsivenessMonitor monitor, int milliseconds)
   {
      Answer(TimeSpan.FromMilliseconds(milliseconds));
      monitor.Check();
   }



   [Fact]
   public void AWindowThatAnswersInTime_LeavesNoLine()
   {
      using ResponsivenessMonitor monitor = Create();

      monitor.Check(); // asks
      Assert.Single(_asked);
      Answer(TimeSpan.FromMilliseconds(40));
      monitor.Check(); // answered in time: asks again
      Answer(TimeSpan.FromMilliseconds(249));
      monitor.Check();

      Assert.Empty(_log.Entries);
      Assert.Single(_asked);
   }



   [Fact]
   public void ALateAnswer_IsReported_WithHowLongItTook_AndWhatWasRunning()
   {
      using ResponsivenessMonitor monitor = Create();
      monitor.Check();
      Stall(monitor, 250);

      monitor.Activity = "syncing 'Engineering'";
      Assert.Equal("syncing 'Engineering'", monitor.Activity);
      QuietPeriodPasses(monitor); // the next stall has its own line again
      Stall(monitor, 1357);

      Assert.Equal(
          [
             ("INFO", "The window did not respond for 250 ms."),
             ("INFO", "The window did not respond for 1357 ms while syncing 'Engineering'."),
          ],
          _log.Entries);
   }



   [Fact]
   public void StallsThatFollowAReportClosely_AreCounted_AndReportedTogether_WhenTheQuietPeriodIsOver()
   {
      using ResponsivenessMonitor monitor = Create();
      monitor.Activity = "syncing 'Engineering'";
      monitor.Check();
      Stall(monitor, 400);
      Stall(monitor, 300);
      Stall(monitor, 500);
      Assert.Single(_log.Entries); // only the first has its own line

      QuietPeriodPasses(monitor);

      Assert.Equal(
          [
             "The window did not respond for 400 ms while syncing 'Engineering'.",
             "Since then the window did not respond 2 more times while syncing 'Engineering': 800 ms in total, the longest 500 ms.",
          ],
          _log.Messages);
   }



   [Fact]
   public void ASingleFollowingStall_IsReportedAsOnceMore()
   {
      using ResponsivenessMonitor monitor = Create();
      monitor.Check();
      Stall(monitor, 400);
      Stall(monitor, 300);

      QuietPeriodPasses(monitor);

      Assert.Equal(
          "Since then the window did not respond once more: 300 ms in total, the longest 300 ms.",
          _log.Messages[^1]);
   }



   [Fact]
   public void AWindowThatStaysSilent_IsReportedOnce_WhileItStillIs_AndAgainWhenItAnswers()
   {
      using ResponsivenessMonitor monitor = Create();
      monitor.Check(); // asks; no answer comes

      Looks(monitor, TimeSpan.FromSeconds(4.9));
      Assert.Empty(_log.Entries); // not long enough yet

      Looks(monitor, TimeSpan.FromSeconds(0.1));
      Looks(monitor, TimeSpan.FromSeconds(1)); // said once, not on every look
      Assert.Equal([("ERROR", "The window has not responded for 5000 ms and still does not.")], _log.Entries);
      Assert.Single(_asked); // no second question while the first is unanswered

      Looks(monitor, TimeSpan.FromSeconds(1));
      Stall(monitor, 0); // it answers, seven seconds after it was asked
      Assert.Equal("The window did not respond for 7000 ms.", _log.Messages[^1]);
   }



   [Fact]
   public void TheCollectedStalls_NameWhatWasRunningThen_NotWhatRunsWhenTheyAreReported()
   {
      using ResponsivenessMonitor monitor = Create();
      monitor.Activity = "syncing 'Engineering'";
      monitor.Check();
      Stall(monitor, 400);
      Stall(monitor, 300);

      monitor.Activity = null; // the sync is over by the time the summary is written
      QuietPeriodPasses(monitor);

      Assert.Equal(
          "Since then the window did not respond once more while syncing 'Engineering': 300 ms in total, the longest 300 ms.",
          _log.Messages[^1]);
   }



   [Fact]
   public void AProcessThatWasNotRunning_IsNotAWindowThatDidNotRespond()
   {
      using ResponsivenessMonitor monitor = Create();
      monitor.Check(); // asks

      // The machine sleeps for an hour: no look, no answer. Then both come back.
      _time.Advance(TimeSpan.FromHours(1));
      _asked.Dequeue()();
      monitor.Check();

      Assert.Empty(_log.Entries); // the question says nothing about the window
      Assert.Single(_asked); // and the watch goes on with a new one

      Stall(monitor, 400);
      Assert.Equal(["The window did not respond for 400 ms."], _log.Messages);
      Assert.Equal(TimeSpan.FromSeconds(2), ResponsivenessMonitor.PauseAfter);
   }



   [Fact]
   public void WhenTheWindowTakesNoMoreWork_TheWatchEnds_AndWhatWasCollectedIsReported()
   {
      using ResponsivenessMonitor monitor = Create();
      monitor.Check();
      Stall(monitor, 400);
      Answer(TimeSpan.FromMilliseconds(300));
      _windowTakesWork = false; // the window is closing
      monitor.Check(); // notes the stall, then finds it cannot ask again

      Assert.Equal(
          "Since then the window did not respond once more: 300 ms in total, the longest 300 ms.",
          _log.Messages[^1]);

      int lines = _log.Entries.Count;
      _windowTakesWork = true;
      monitor.Check(); // over: it does not start again
      Assert.Empty(_asked);
      Assert.Equal(lines, _log.Entries.Count);
   }



   [Fact]
   public void Dispose_ReportsWhatWasCollected_StopsTheTimer_AndIsSafeTwice()
   {
      ResponsivenessMonitor monitor = Create();
      ManualTime.ManualTimer timer = Assert.Single(_time.Timers);
      Assert.Equal(ResponsivenessMonitor.DefaultInterval, timer.DueTime);
      Assert.Equal(ResponsivenessMonitor.DefaultInterval, timer.Period);

      timer.Fire(); // the timer is what looks: it asks
      Assert.Single(_asked);
      Stall(monitor, 400);
      Stall(monitor, 300);

      monitor.Dispose();
      monitor.Dispose();

      Assert.True(timer.IsDisposed);
      Assert.Equal(2, _log.Entries.Count);
      Assert.StartsWith("Since then the window did not respond once more", _log.Messages[^1], StringComparison.Ordinal);

      monitor.Check(); // a look that was already on its way finds the watch over
      Assert.Equal(2, _log.Entries.Count);
   }



   [Fact]
   public void WithNothingGiven_ItUsesTheSystemClock_AndItsOwnDefaults()
   {
      using var monitor = new ResponsivenessMonitor(_ => true, _log);

      Assert.Null(monitor.Activity);
      Assert.Equal(TimeSpan.FromMilliseconds(250), ResponsivenessMonitor.DefaultThreshold);
      Assert.Equal(TimeSpan.FromSeconds(5), ResponsivenessMonitor.StillWaitingAfter);
   }



   [Fact]
   public void AMissingQueueOrLog_IsRefused()
   {
      Assert.Throws<ArgumentNullException>(() => new ResponsivenessMonitor(null!, _log));
      Assert.Throws<ArgumentNullException>(() => new ResponsivenessMonitor(_ => true, null!));
   }
}
