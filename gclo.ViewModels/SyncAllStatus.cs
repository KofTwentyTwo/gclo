/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;


namespace gclo.ViewModels;


/// <summary>
/// What a sync-all pass is doing right now, in words a status bar can show for the
/// whole pass: which account, which of how many, how far along, and afterwards how it
/// ended (#167). <see cref="SyncAllCoordinator"/> drives it; the window binds to it,
/// so a pass is visible whichever workspace is on screen. Used on the window's thread
/// only.
/// </summary>
public sealed partial class SyncAllStatus : ObservableObject
{
   /// <summary>The account in flight; its progress is followed while it is.</summary>
   private WorkspaceViewModel? _current;

   private int _position;

   private int _accounts;



   /// <summary>Creates a status with nothing to show.</summary>
   public SyncAllStatus()
   {
      Title = "";
      Message = "";
   }



   /// <summary>True from the start of a pass until its last account is done.</summary>
   [ObservableProperty]
   public partial bool IsRunning { get; private set; }

   /// <summary>
   /// True while there is something to show: during a pass, and afterwards until the
   /// result is closed (which the view does by setting it to false).
   /// </summary>
   [ObservableProperty]
   public partial bool IsOpen { get; set; }

   /// <summary>Running, or how the last pass ended.</summary>
   [ObservableProperty]
   public partial SyncAllOutcome Outcome { get; private set; }

   /// <summary>The headline, for example "Sync all: account 2 of 3".</summary>
   [ObservableProperty]
   public partial string Title { get; private set; }

   /// <summary>The detail, for example "Engineering: 120 of 281 repositories.".</summary>
   [ObservableProperty]
   public partial string Message { get; private set; }

   /// <summary>The account in flight, for a link to its workspace; null between accounts and after the pass.</summary>
   [ObservableProperty]
   public partial Guid? CurrentAccountId { get; private set; }

   /// <summary>
   /// How far the account in flight is, from 0 to 1; null while that cannot be said
   /// (its repositories are being listed, or nothing is in flight).
   /// </summary>
   [ObservableProperty]
   public partial double? Fraction { get; private set; }

   /// <summary>True once the user asked for the pass to stop; the account in flight still finishes.</summary>
   [ObservableProperty]
   public partial bool IsCancelRequested { get; private set; }



   /// <summary>A pass over <paramref name="accounts"/> accounts starts.</summary>
   /// <param name="accounts">How many accounts are scheduled.</param>
   public void Begin(int accounts)
   {
      Release();
      _accounts = accounts;
      _position = 0;
      CurrentAccountId = null;
      Fraction = null;
      IsCancelRequested = false;
      Outcome = SyncAllOutcome.Running;
      Title = "Sync all";
      Message = $"Starting {Count(accounts, "account")}.";
      IsRunning = true;
      IsOpen = true;
   }



   /// <summary>The pass reached an account; its progress is followed until the next call.</summary>
   /// <param name="position">Its place in the pass, from 1.</param>
   /// <param name="workspace">Its workspace.</param>
   public void Enter(int position, WorkspaceViewModel workspace)
   {
      ArgumentNullException.ThrowIfNull(workspace);
      Release();
      _position = position;
      _current = workspace;
      workspace.PropertyChanged += OnCurrentChanged;
      CurrentAccountId = workspace.AccountId;
      Refresh();
   }



   /// <summary>The user asked for the pass to stop. Says so; the account in flight still finishes.</summary>
   public void RequestCancel()
   {
      if(!IsRunning)
      {
         return;
      }
      IsCancelRequested = true;
      Refresh();
   }



   /// <summary>The pass ended; the result stays until it is closed.</summary>
   /// <param name="result">How it ended.</param>
   public void End(SyncAllResult result)
   {
      ArgumentNullException.ThrowIfNull(result);
      Leave();
      Outcome = result.WasCanceled ? SyncAllOutcome.Canceled
          : result.Failed > 0 ? SyncAllOutcome.FinishedWithFailures
          : SyncAllOutcome.Finished;
      Title = result.WasCanceled ? "Sync all canceled" : "Sync all finished";
      Message = Describe(result);
      IsOpen = true;
   }



   /// <summary>The pass stopped on an unexpected error.</summary>
   /// <param name="reason">What went wrong.</param>
   public void Stop(string reason)
   {
      Leave();
      Outcome = SyncAllOutcome.Stopped;
      Title = "Sync all stopped";
      Message = reason;
      IsOpen = true;
   }



   /// <summary>The sentence that sums up a finished or canceled pass.</summary>
   internal static string Describe(SyncAllResult result)
   {
      string summary = $"{Count(result.Ran, "account")} synced, {result.Skipped.ToString(CultureInfo.InvariantCulture)} skipped.";
      return result.Failed switch
      {
         0 => summary,
         1 => summary + " 1 account needs attention; it is marked in the pane.",
         _ => summary + $" {result.Failed.ToString(CultureInfo.InvariantCulture)} accounts need attention; they are marked in the pane.",
      };
   }



   private static string Count(int number, string noun)
       => number == 1 ? $"1 {noun}" : $"{number.ToString(CultureInfo.InvariantCulture)} {noun}s";



   private void OnCurrentChanged(object? sender, PropertyChangedEventArgs e)
   {
      if(e.PropertyName is nameof(WorkspaceViewModel.IsLoadingRepos)
          or nameof(WorkspaceViewModel.IsRunning)
          or nameof(WorkspaceViewModel.CompletedCount)
          or nameof(WorkspaceViewModel.TotalCount))
      {
         Refresh();
      }
   }



   private void Refresh()
   {
      if(_current is not { } workspace)
      {
         return;
      }

      // A run that has just begun still carries the counts of the one before for a
      // moment (more done than there is to do): that is 'starting', not a fraction.
      string doing;
      if(workspace.IsRunning && workspace.CompletedCount <= workspace.TotalCount)
      {
         doing = $"{workspace.CompletedCount.ToString(CultureInfo.InvariantCulture)} of "
             + (workspace.TotalCount == 1 ? "1 repository" : $"{workspace.TotalCount.ToString(CultureInfo.InvariantCulture)} repositories");
         Fraction = workspace.TotalCount > 0 ? (double)workspace.CompletedCount / workspace.TotalCount : null;
      }
      else
      {
         doing = workspace.IsLoadingRepos ? "listing its repositories" : "starting";
         Fraction = null;
      }

      Title = $"Sync all: account {_position.ToString(CultureInfo.InvariantCulture)} of {_accounts.ToString(CultureInfo.InvariantCulture)}";
      Message = $"{workspace.DisplayName}: {doing}."
          + (IsCancelRequested ? " Stopping when this account is done." : "");
   }



   /// <summary>Nothing is in flight any more.</summary>
   private void Leave()
   {
      Release();
      CurrentAccountId = null;
      Fraction = null;
      IsRunning = false;
   }



   private void Release()
   {
      if(_current is { } workspace)
      {
         workspace.PropertyChanged -= OnCurrentChanged;
         _current = null;
      }
   }
}
