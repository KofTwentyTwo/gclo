/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>
/// What one run reports through. Every report is passed on, except a report for a
/// repository that has already reported its final state in this run: a git
/// operation that was abandoned on cancellation keeps running for a while and
/// keeps reporting transfer progress, and without this a row that says Canceled
/// would go back to "Cloning 40%". A repository therefore ends a run in exactly
/// one final state, and nothing follows it.
/// </summary>
internal sealed class RunProgress : IProgress<RepoProgress>
{
   private readonly Lock _gate = new();

   private readonly IProgress<RepoProgress> _inner;

   // The engine treats names that differ only by case as one repository.
   private readonly HashSet<string> _finished = new(StringComparer.OrdinalIgnoreCase);



   /// <param name="inner">The caller's progress, which receives what is passed on.</param>
   public RunProgress(IProgress<RepoProgress> inner)
   {
      ArgumentNullException.ThrowIfNull(inner);
      _inner = inner;
   }



   /// <inheritdoc/>
   public void Report(RepoProgress value)
   {
      ///////////////////////////////////////////////////////////////////////////////
      // Passed on inside the lock: a progress report racing the final one on      //
      // another thread is then either before it, or dropped; never after it.      //
      ///////////////////////////////////////////////////////////////////////////////
      lock(_gate)
      {
         if(_finished.Contains(value.RepoName))
         {
            return;
         }

         _inner.Report(value);

         // Only a final state the caller has actually been given counts: if its
         // progress threw, the repository has not finished as far as it knows, and
         // the failure the engine then reports for it must still get through.
         if(value.Status is SyncStatus.Done or SyncStatus.Failed or SyncStatus.Canceled)
         {
            _finished.Add(value.RepoName);
         }
      }
   }
}
