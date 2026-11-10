/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine.Tests;


/// <summary>
/// <see cref="RunProgress"/> passes every report on, except what arrives for a
/// repository after its final state in the run (#153).
/// </summary>
public sealed class RunProgressTests
{
   private readonly RecordingProgress _seen = new();



   private RunProgress NewRun() => new(_seen);



   [Fact]
   public void Report_BeforeTheFinalState_IsPassedOn_InOrder()
   {
      RunProgress run = NewRun();

      run.Report(new RepoProgress("alpha", SyncStatus.Queued));
      run.Report(new RepoProgress("alpha", SyncStatus.Cloning));
      run.Report(new RepoProgress("alpha", SyncStatus.Cloning, Percent: 0.5));
      run.Report(new RepoProgress("alpha", SyncStatus.Done));

      Assert.Equal([SyncStatus.Queued, SyncStatus.Cloning, SyncStatus.Cloning, SyncStatus.Done], _seen.StatusesFor("alpha"));
      Assert.Equal(0.5, _seen.Reports[2].Percent);
   }



   [Theory]
   [InlineData(SyncStatus.Done)]
   [InlineData(SyncStatus.Failed)]
   [InlineData(SyncStatus.Canceled)]
   public void Report_AfterAFinalState_IsDropped_WhateverItSays(SyncStatus final)
   {
      RunProgress run = NewRun();
      run.Report(new RepoProgress("alpha", final));

      run.Report(new RepoProgress("alpha", SyncStatus.Cloning, Percent: 0.9));
      run.Report(new RepoProgress("alpha", SyncStatus.Pulling));
      run.Report(new RepoProgress("alpha", SyncStatus.Queued));
      run.Report(new RepoProgress("alpha", SyncStatus.Done));
      run.Report(new RepoProgress("alpha", SyncStatus.Failed, "late"));
      run.Report(new RepoProgress("ALPHA", SyncStatus.Cloning)); // one repository, whatever its case

      Assert.Equal([final], _seen.StatusesFor("alpha"));
      Assert.Empty(_seen.StatusesFor("ALPHA"));
   }



   [Fact]
   public void Report_ForAnotherRepository_IsNotHeldBack()
   {
      RunProgress run = NewRun();
      run.Report(new RepoProgress("alpha", SyncStatus.Canceled));

      run.Report(new RepoProgress("bravo", SyncStatus.Cloning));
      run.Report(new RepoProgress("bravo", SyncStatus.Done));

      Assert.Equal([SyncStatus.Cloning, SyncStatus.Done], _seen.StatusesFor("bravo"));
   }



   [Fact]
   public void Report_FromManyThreads_NeverPutsProgressAfterTheFinalState()
   {
      for(int round = 0; round < 50; round++)
      {
         var seen = new RecordingProgress();
         var run = new RunProgress(seen);

         Parallel.Invoke(
             () =>
             {
                for(int i = 0; i < 200; i++)
                {
                   run.Report(new RepoProgress("alpha", SyncStatus.Cloning, Percent: i / 200.0));
                }
             },
             () => run.Report(new RepoProgress("alpha", SyncStatus.Canceled)));

         IReadOnlyList<SyncStatus> statuses = seen.StatusesFor("alpha");
         Assert.Equal(SyncStatus.Canceled, statuses[^1]);
         Assert.Single(statuses, s => s == SyncStatus.Canceled);
      }
   }



   /// <summary>A caller's progress that fails on the first final state it is given.</summary>
   private sealed class ThrowsOnDone(RecordingProgress seen) : IProgress<RepoProgress>
   {
      public void Report(RepoProgress value)
      {
         if(value.Status == SyncStatus.Done)
         {
            throw new InvalidOperationException("the caller's progress failed");
         }
         seen.Report(value);
      }
   }



   [Fact]
   public void Report_AFinalStateTheCallerDidNotTake_DoesNotCount_SoTheNextOneGetsThrough()
   {
      var run = new RunProgress(new ThrowsOnDone(_seen));

      Assert.Throws<InvalidOperationException>(() => run.Report(new RepoProgress("alpha", SyncStatus.Done)));
      run.Report(new RepoProgress("alpha", SyncStatus.Failed, "the caller's progress failed"));
      run.Report(new RepoProgress("alpha", SyncStatus.Cloning));

      Assert.Equal([SyncStatus.Failed], _seen.StatusesFor("alpha"));
   }



   [Fact]
   public void Constructor_RefusesAMissingProgress()
       => Assert.Throws<ArgumentNullException>(() => new RunProgress(null!));
}
