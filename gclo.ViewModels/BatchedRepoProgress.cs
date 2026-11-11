/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.Engine;


namespace gclo.ViewModels;


/// <summary>
/// Hands a sync's progress reports to the window a few times a second instead of one
/// by one. Reports come from the engine's worker threads, thousands per run (a clone
/// reports every whole percent), and each one delivered on its own costs the window's
/// thread a row update and a layout pass: that is what stopped the window answering
/// during a large run (#167). Reports wait here for at most the interval, are then
/// delivered together on the thread this instance was created on, and of several for
/// one repository only the latest is delivered. Every report carries the repository's
/// whole state, so nothing is lost with the ones it replaced.
/// </summary>
public sealed class BatchedRepoProgress : IProgress<RepoProgress>, IDisposable
{
   /// <summary>How long a report waits at most before it is delivered.</summary>
   public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(100);

   private readonly Action<RepoProgress> _handler;

   private readonly SynchronizationContext _context;

   private readonly TimeSpan _interval;

   private readonly ITimer _timer;

   private readonly Lock _gate = new();

   /// <summary>Reports not delivered yet, oldest first. Guarded by <see cref="_gate"/>.</summary>
   private List<RepoProgress> _pending = [];

   /// <summary>True while a delivery is on its way. Guarded by <see cref="_gate"/>.</summary>
   private bool _deliveryScheduled;

   /// <summary>True once the timer is gone. Guarded by <see cref="_gate"/>.</summary>
   private bool _disposed;



   /// <summary>Creates the progress; call it on the thread the reports are to be delivered on.</summary>
   /// <param name="handler">Receives each delivered report, on the delivery thread.</param>
   /// <param name="context">Where to deliver; the calling thread's context by default.</param>
   /// <param name="interval">How long a report waits at most; <see cref="DefaultInterval"/> by default.</param>
   /// <param name="timeProvider">The timer source; the system's by default.</param>
   public BatchedRepoProgress(
       Action<RepoProgress> handler,
       SynchronizationContext? context = null,
       TimeSpan? interval = null,
       TimeProvider? timeProvider = null)
   {
      ArgumentNullException.ThrowIfNull(handler);
      _handler = handler;
      _context = context ?? SynchronizationContext.Current ?? new SynchronizationContext();
      _interval = interval ?? DefaultInterval;
      _timer = (timeProvider ?? TimeProvider.System).CreateTimer(
          _ => PostDelivery(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
   }



   /// <summary>Takes a report from any thread; it is delivered with the next batch.</summary>
   /// <param name="value">The report.</param>
   public void Report(RepoProgress value)
   {
      ArgumentNullException.ThrowIfNull(value);

      bool timerIsGone;
      lock(_gate)
      {
         _pending.Add(value);
         if(_deliveryScheduled)
         {
            return;
         }
         _deliveryScheduled = true;
         timerIsGone = _disposed;
         if(!timerIsGone)
         {
            _timer.Change(_interval, Timeout.InfiniteTimeSpan);
         }
      }

      if(timerIsGone)
      {
         // After the run (an abandoned operation that still finished): nothing is
         // left to batch it with.
         PostDelivery();
      }
   }



   /// <summary>
   /// Delivers what is still waiting, now, on the calling thread, and stops the timer.
   /// Call it on the thread this instance was created on when the run has ended, so
   /// the rows are final before the outcome is stated. A report that still arrives
   /// afterwards is delivered on its own.
   /// </summary>
   public void Dispose()
   {
      lock(_gate)
      {
         _disposed = true;
      }
      _timer.Dispose();
      Deliver();
   }



   /// <summary>
   /// Of several reports for one repository, the latest; repositories stay in the
   /// order in which each was first reported.
   /// </summary>
   internal static List<RepoProgress> LatestPerRepository(List<RepoProgress> reports)
   {
      var latest = new List<RepoProgress>(reports.Count);
      var slots = new Dictionary<string, int>(StringComparer.Ordinal);
      foreach(RepoProgress report in reports)
      {
         if(slots.TryGetValue(report.RepoName, out int slot))
         {
            latest[slot] = report;
         }
         else
         {
            slots[report.RepoName] = latest.Count;
            latest.Add(report);
         }
      }
      return latest;
   }



   private void PostDelivery() => _context.Post(_ => Deliver(), null);



   private void Deliver()
   {
      List<RepoProgress> batch;
      lock(_gate)
      {
         batch = _pending;
         _pending = [];
         _deliveryScheduled = false;
      }

      // Every report of the batch is handed over, even when the handler fails for
      // one of them: the others are final states of other repositories, and losing
      // them would leave rows saying 'cloning' for ever. The first failure is then
      // thrown where a single failing report would have thrown it.
      System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
      foreach(RepoProgress report in LatestPerRepository(batch))
      {
         try
         {
            _handler(report);
         }
         catch(Exception ex)
         {
            failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
         }
      }
      failure?.Throw();
   }
}
