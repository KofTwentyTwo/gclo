/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine.Tests;


/// <summary>
/// A stand-in for the window's thread: what is posted to it waits until the test
/// runs it, the way a dispatcher queue holds work until the thread gets to it.
/// </summary>
internal sealed class PumpedContext : SynchronizationContext
{
   private readonly Queue<(SendOrPostCallback Callback, object? State)> _posted = new();



   /// <summary>How many posted callbacks wait to be run.</summary>
   public int Waiting
   {
      get
      {
         lock(_posted)
         {
            return _posted.Count;
         }
      }
   }



   public override void Post(SendOrPostCallback d, object? state)
   {
      lock(_posted)
      {
         _posted.Enqueue((d, state));
      }
   }



   /// <summary>Runs everything posted so far, on the calling thread; returns how many callbacks ran.</summary>
   public int RunAll()
   {
      int ran = 0;
      while(true)
      {
         (SendOrPostCallback Callback, object? State) next;
         lock(_posted)
         {
            if(_posted.Count == 0)
            {
               return ran;
            }
            next = _posted.Dequeue();
         }
         next.Callback(next.State);
         ran++;
      }
   }
}
