/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.ViewModels;


namespace gclo.Engine.Tests;


/// <summary>
/// <see cref="BatchedRepoProgress"/> holds progress reports for a moment and hands
/// them to the window together, the latest per repository, so thousands of reports
/// do not cost the window's thread thousands of row updates (#167).
/// </summary>
public sealed class BatchedRepoProgressTests
{
   private static readonly TimeSpan s_interval = TimeSpan.FromMilliseconds(100);

   private readonly ManualTime _time = new();

   private readonly PumpedContext _window = new();

   private readonly List<RepoProgress> _delivered = [];



   private BatchedRepoProgress Create() => new(_delivered.Add, _window, s_interval, _time);



   private ManualTime.ManualTimer Timer => Assert.Single(_time.Timers);



   [Fact]
   public void Reports_Wait_AndAreDeliveredTogether_OnTheWindowsThread_WhenTheTimerFires()
   {
      using BatchedRepoProgress progress = Create();
      Assert.False(Timer.IsArmed); // nothing to deliver yet

      progress.Report(new RepoProgress("alpha", SyncStatus.Cloning));
      progress.Report(new RepoProgress("bravo", SyncStatus.Pulling));

      Assert.Empty(_delivered);
      Assert.True(Timer.IsArmed);
      Assert.Equal(s_interval, Timer.DueTime);
      Assert.Equal(1, Timer.Changes); // the second report did not start the wait again

      Timer.Fire(); // on a timer thread: nothing but a post to the window's thread
      Assert.Empty(_delivered);
      Assert.Equal(1, _window.Waiting);

      _window.RunAll();
      Assert.Equal(["alpha", "bravo"], _delivered.Select(r => r.RepoName), StringComparer.Ordinal);
   }



   [Fact]
   public void OfSeveralReportsForOneRepository_OnlyTheLatestIsDelivered_InTheOrderRepositoriesFirstReported()
   {
      using BatchedRepoProgress progress = Create();

      progress.Report(new RepoProgress("alpha", SyncStatus.Cloning, Percent: 0.10));
      progress.Report(new RepoProgress("bravo", SyncStatus.Cloning, Percent: 0.50));
      progress.Report(new RepoProgress("alpha", SyncStatus.Cloning, Percent: 0.90));
      progress.Report(new RepoProgress("alpha", SyncStatus.Failed, Error: "boom"));
      Timer.Fire();
      _window.RunAll();

      Assert.Equal(
          [new RepoProgress("alpha", SyncStatus.Failed, Error: "boom"), new RepoProgress("bravo", SyncStatus.Cloning, Percent: 0.50)],
          _delivered);
   }



   [Fact]
   public void AfterADelivery_TheNextReport_StartsANewWait()
   {
      using BatchedRepoProgress progress = Create();
      progress.Report(new RepoProgress("alpha", SyncStatus.Cloning));
      Timer.Fire();
      _window.RunAll();
      Assert.False(Timer.IsArmed);

      progress.Report(new RepoProgress("alpha", SyncStatus.Done));

      Assert.True(Timer.IsArmed);
      Assert.Equal(2, Timer.Changes);
      Timer.Fire();
      _window.RunAll();
      Assert.Equal([SyncStatus.Cloning, SyncStatus.Done], _delivered.Select(r => r.Status));
   }



   [Fact]
   public void AReportThatArrivesWhileADeliveryIsPosted_GoesWithThatDelivery()
   {
      using BatchedRepoProgress progress = Create();
      progress.Report(new RepoProgress("alpha", SyncStatus.Cloning));
      Timer.Fire(); // posted, not run yet

      progress.Report(new RepoProgress("bravo", SyncStatus.Cloning));
      Assert.False(Timer.IsArmed); // one delivery is already on its way

      _window.RunAll();
      Assert.Equal(["alpha", "bravo"], _delivered.Select(r => r.RepoName), StringComparer.Ordinal);
   }



   [Fact]
   public void Dispose_DeliversWhatStillWaits_AtOnce_OnTheCallingThread_AndStopsTheTimer()
   {
      BatchedRepoProgress progress = Create();
      progress.Report(new RepoProgress("alpha", SyncStatus.Done));
      progress.Report(new RepoProgress("bravo", SyncStatus.Failed, Error: "boom"));

      progress.Dispose();

      Assert.Equal(["alpha", "bravo"], _delivered.Select(r => r.RepoName), StringComparer.Ordinal);
      Assert.Equal(0, _window.Waiting); // not through the queue: the caller is the window's thread
      Assert.True(Timer.IsDisposed);

      progress.Dispose(); // a second time changes nothing
      Assert.Equal(2, _delivered.Count);
   }



   [Fact]
   public void ADeliveryPostedBeforeDispose_FindsNothingLeft_WhenItRuns()
   {
      BatchedRepoProgress progress = Create();
      progress.Report(new RepoProgress("alpha", SyncStatus.Done));
      Timer.Fire(); // posted
      progress.Dispose(); // delivered here

      _window.RunAll();

      Assert.Single(_delivered);
   }



   [Fact]
   public void AReportAfterDispose_IsDeliveredOnItsOwn_ThroughTheWindowsQueue()
   {
      BatchedRepoProgress progress = Create();
      progress.Dispose();

      // An operation that was abandoned and still finished reports after the run.
      progress.Report(new RepoProgress("alpha", SyncStatus.Done));
      progress.Report(new RepoProgress("bravo", SyncStatus.Done));

      Assert.Empty(_delivered);
      Assert.Equal(1, _window.Waiting); // one post for both: the first is still on its way
      _window.RunAll();
      Assert.Equal(["alpha", "bravo"], _delivered.Select(r => r.RepoName), StringComparer.Ordinal);
   }



   [Fact]
   public async Task WithNothingGiven_ItUsesTheCallersContext_TheSystemTimer_AndTheDefaultInterval()
   {
      var delivered = new TaskCompletionSource<RepoProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
      using var progress = new BatchedRepoProgress(delivered.SetResult);

      progress.Report(new RepoProgress("alpha", SyncStatus.Done));
      RepoProgress report = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(30));

      Assert.Equal("alpha", report.RepoName);
      Assert.Equal(TimeSpan.FromMilliseconds(100), BatchedRepoProgress.DefaultInterval);
   }



   [Fact]
   public void AHandlerThatFailsForOneReport_StillGetsTheOthers_AndTheFailureIsNotSwallowed()
   {
      var delivered = new List<string>();
      using var progress = new BatchedRepoProgress(
          report =>
          {
             if(string.Equals(report.RepoName, "alpha", StringComparison.Ordinal))
             {
                throw new InvalidOperationException("the row for alpha is gone");
             }
             delivered.Add(report.RepoName);
          },
          _window, s_interval, _time);
      progress.Report(new RepoProgress("alpha", SyncStatus.Done));
      progress.Report(new RepoProgress("bravo", SyncStatus.Done));
      progress.Report(new RepoProgress("charlie", SyncStatus.Failed, Error: "boom"));
      Timer.Fire();

      InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => _window.RunAll());

      Assert.Equal("the row for alpha is gone", failure.Message);
      Assert.Equal(["bravo", "charlie"], delivered, StringComparer.Ordinal); // their final states were not lost
   }



   [Fact]
   public void AMissingHandlerOrReport_IsRefused()
   {
      Assert.Throws<ArgumentNullException>(() => new BatchedRepoProgress(null!));
      using BatchedRepoProgress progress = Create();
      Assert.Throws<ArgumentNullException>(() => progress.Report(null!));
   }
}
