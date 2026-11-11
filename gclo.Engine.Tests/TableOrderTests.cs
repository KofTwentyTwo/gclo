/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.ViewModels;
using static gclo.Engine.Tests.GitTestHelpers;


namespace gclo.Engine.Tests;


/// <summary>
/// The repository table's default order (#168), checked against the rule as it is
/// written here, not against the view model's own comparison: while a sync runs, the
/// rows in flight (the one that started first on top), then the failed ones and the
/// finished ones (the latest first in each), then the ones waiting, by name; with no
/// sync running, the rows in flight, the failed ones, and then the rest, by name.
/// Every report is made on the test's thread through the view model's run seam, so
/// each step is checked the moment it happened.
/// </summary>
public sealed class TableOrderTests : IDisposable
{
   private readonly FakeRepositoryLister _lister = new();

   private readonly string _folder = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

   private readonly List<WorkspaceViewModel> _made = [];

   /// <summary>What the test knows about each row: its status, and when it last started or finished.</summary>
   private readonly Dictionary<string, (SyncStatus Status, long Activity)> _rows = new(StringComparer.Ordinal);

   private TaskCompletionSource<SyncSummary>? _run;

   private IProgress<RepoProgress>? _progress;

   private List<string> _runNames = [];

   private long _clock;



   public void Dispose()
   {
      foreach(WorkspaceViewModel vm in _made)
      {
         vm.Dispose();
      }
      TryDeleteDirectory(_folder);
   }



   /// <summary>Hands each report to the view model at once, on the reporting (test) thread.</summary>
   private sealed class Inline(Action<RepoProgress> handler) : IProgress<RepoProgress>
   {
      public void Report(RepoProgress value) => handler(value);
   }



   /// <summary>A loaded workspace whose sync runs are driven by the test, report by report.</summary>
   private async Task<WorkspaceViewModel> LoadAsync(params string[] names)
   {
      _lister.Repositories = [.. names.Select(n => Repo(n))];
      var vm = new WorkspaceViewModel(
          _lister, new FakeGitClient(), new FakeOrganizationLister(),
          handler => new Inline(handler), TimeSpan.FromMilliseconds(1), new NullActivityLog(), wsl: new FakeWslCloner());
      _made.Add(vm);
      vm.Organization = "acme";
      vm.Token = "token-1234567890";
      await WaitUntilAsync(() => vm.StatusText.Length > 0, "org lookup to settle");
      vm.TargetFolder = _folder;
      vm.SyncRunner = (_, repos, progress, _) =>
      {
         _run = new TaskCompletionSource<SyncSummary>();
         _progress = progress;
         _runNames = [.. repos.Select(r => r.Name)];
         foreach(string name in _runNames)
         {
            // The run takes its rows back to waiting; when they last finished stays as it was.
            _rows[name] = (SyncStatus.Queued, _rows[name].Activity);
            progress.Report(new RepoProgress(name, SyncStatus.Queued));
         }
         return _run.Task;
      };
      await vm.LoadReposCommand.ExecuteAsync(null);
      foreach(string name in names)
      {
         _rows[name] = (SyncStatus.Queued, 0);
      }
      return vm;
   }



   private void Report(string name, SyncStatus status)
   {
      if(_rows[name].Status != status)
      {
         _rows[name] = (status, ++_clock);
      }
      _progress!.Report(new RepoProgress(name, status, status == SyncStatus.Failed ? "boom" : null));
   }



   /// <summary>Ends the run in flight; rows that never finished are reported canceled, as the engine does.</summary>
   private void EndRun()
   {
      foreach(string name in _runNames)
      {
         if(_rows[name].Status is not (SyncStatus.Done or SyncStatus.Failed or SyncStatus.Canceled))
         {
            Report(name, SyncStatus.Canceled);
         }
      }
      TaskCompletionSource<SyncSummary> run = _run!;
      _run = null;
      run.SetResult(new SyncSummary(_runNames.Count, 0, 0, 0, 0, WasCanceled: false));
   }



   /// <summary>The run in flight stops on an error, with its rows left as they are.</summary>
   private void FailRun()
   {
      TaskCompletionSource<SyncSummary> run = _run!;
      _run = null;
      run.SetException(new InvalidOperationException("the run failed"));
   }



   /// <summary>The rule, written out: which names the table shows, in which order.</summary>
   private string[] Expected(RepoFilter filter = RepoFilter.All)
   {
      bool running = _run is not null;

      int Group(SyncStatus status) => status switch
      {
         SyncStatus.Cloning or SyncStatus.Pulling => 0,
         SyncStatus.Failed => 1,
         SyncStatus.Done or SyncStatus.Canceled when running => 2,
         _ => 3,
      };

      long WithinGroup((SyncStatus Status, long Activity) row) => Group(row.Status) switch
      {
         0 => row.Activity, // the one that started first stays on top
         1 or 2 when running => -row.Activity, // the latest first
         _ => 0, // by name
      };

      return
      [
         .. _rows
             .Where(row => filter switch
             {
                RepoFilter.Active => row.Value.Status is SyncStatus.Cloning or SyncStatus.Pulling,
                RepoFilter.Failed => row.Value.Status == SyncStatus.Failed,
                RepoFilter.Pending => row.Value.Status == SyncStatus.Queued,
                _ => true,
             })
             .OrderBy(row => Group(row.Value.Status))
             .ThenBy(row => WithinGroup(row.Value))
             .ThenBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
             .Select(row => row.Key),
      ];
   }



   private static string[] Table(WorkspaceViewModel vm) => [.. vm.FilteredRepos.Select(r => r.Name)];



   [Fact]
   public async Task SeveralInFlight_OneFinishing_OneFailing_OneCanceled_EachRowIsWhereTheRulePutsIt()
   {
      WorkspaceViewModel vm = await LoadAsync("alpha", "bravo", "charlie", "delta", "echo");
      Task sync = vm.SyncCommand.ExecuteAsync(null);
      Assert.Equal(["alpha", "bravo", "charlie", "delta", "echo"], Table(vm));

      Report("bravo", SyncStatus.Cloning);
      Report("alpha", SyncStatus.Pulling);
      Assert.Equal(["bravo", "alpha", "charlie", "delta", "echo"], Table(vm)); // bravo started first: it stays on top

      Report("bravo", SyncStatus.Done); // moves down, below the one still in flight
      Assert.Equal(["alpha", "bravo", "charlie", "delta", "echo"], Table(vm));

      Report("echo", SyncStatus.Cloning); // from the last place to the end of the rows in flight
      Assert.Equal(["alpha", "echo", "bravo", "charlie", "delta"], Table(vm));

      Report("alpha", SyncStatus.Failed); // failed: below the rows in flight, above the finished
      Assert.Equal(["echo", "alpha", "bravo", "charlie", "delta"], Table(vm));

      Report("echo", SyncStatus.Canceled); // finished after bravo: above it
      Assert.Equal(["alpha", "echo", "bravo", "charlie", "delta"], Table(vm));

      Report("delta", SyncStatus.Cloning);
      Report("delta", SyncStatus.Done); // the latest of the finished
      Assert.Equal(["alpha", "delta", "echo", "bravo", "charlie"], Table(vm));
      Assert.Equal(Expected(), Table(vm));

      EndRun(); // charlie never ran: canceled
      await sync;

      Assert.Equal(["alpha", "bravo", "charlie", "delta", "echo"], Table(vm)); // the failed one first, then by name
      Assert.Equal(SyncStatus.Failed, vm.FilteredRepos[0].Status);
   }



   [Fact]
   public async Task ASecondRun_KeepsWhatTheFirstFinished_AboveWhatItWaitsFor_AndTheRetriedRowGoesOnTop()
   {
      WorkspaceViewModel vm = await LoadAsync("alpha", "bravo", "charlie");
      Task first = vm.SyncCommand.ExecuteAsync(null);
      Report("alpha", SyncStatus.Cloning);
      Report("alpha", SyncStatus.Done);
      Report("bravo", SyncStatus.Cloning);
      Report("bravo", SyncStatus.Failed);
      Report("charlie", SyncStatus.Cloning);
      Report("charlie", SyncStatus.Done);
      EndRun();
      await first;
      Assert.Equal(["bravo", "alpha", "charlie"], Table(vm));

      Task retry = vm.RetryFailedCommand.ExecuteAsync(null); // bravo alone
      Assert.Equal(["bravo"], _runNames);
      Assert.Equal(["charlie", "alpha", "bravo"], Table(vm)); // finished (the latest first), then the one waiting

      Report("bravo", SyncStatus.Pulling);
      Assert.Equal(["bravo", "charlie", "alpha"], Table(vm));
      Report("bravo", SyncStatus.Done);
      Assert.Equal(["bravo", "charlie", "alpha"], Table(vm)); // now the latest of the finished
      EndRun();
      await retry;

      Assert.Equal(["alpha", "bravo", "charlie"], Table(vm));
   }



   [Fact]
   public async Task ARunThatStopsOnAnError_LeavesTheTableInTheOrderOfNoRun()
   {
      WorkspaceViewModel vm = await LoadAsync("alpha", "bravo", "charlie");
      Task sync = vm.SyncCommand.ExecuteAsync(null);
      Report("charlie", SyncStatus.Cloning);
      Report("charlie", SyncStatus.Done);
      Report("bravo", SyncStatus.Cloning);
      Report("bravo", SyncStatus.Failed);
      Assert.Equal(["bravo", "charlie", "alpha"], Table(vm));

      FailRun();
      await sync;

      Assert.Equal(RunResultKind.Error, vm.ResultKind);
      Assert.Equal(["bravo", "alpha", "charlie"], Table(vm));
      Assert.Equal(Expected(), Table(vm));
   }



   /// <summary>
   /// Many runs of random steps: starts with a random selection, reports in any
   /// order the engine could make them, filters, ends and failures. After every step
   /// the table is what the rule says, row for row.
   /// </summary>
   [Theory]
   [InlineData(1)]
   [InlineData(2)]
   [InlineData(3)]
   [InlineData(4)]
   [InlineData(5)]
   [InlineData(6)]
   [InlineData(7)]
   [InlineData(8)]
   public async Task WhateverHappens_InWhateverOrder_TheTableIsWhatTheRuleSays(int seed)
   {
      string[] names = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima"];
      WorkspaceViewModel vm = await LoadAsync(names);
      var random = new Random(seed);
      RepoFilter[] filters = [RepoFilter.All, RepoFilter.Active, RepoFilter.Failed, RepoFilter.Pending];
      Task? sync = null;

      for(int step = 0; step < 400; step++)
      {
         int roll = random.Next(100);
         if(roll < 8)
         {
            vm.Filter = filters[random.Next(filters.Length)];
         }
         else if(_run is null)
         {
            foreach(RepoItemViewModel row in vm.Repos)
            {
               row.IsSelected = random.Next(10) < 7;
            }
            vm.Repos[random.Next(vm.Repos.Count)].IsSelected = true;
            sync = vm.SyncCommand.ExecuteAsync(null);
         }
         else if(roll < 14)
         {
            EndRun();
            await sync!;
         }
         else if(roll < 17)
         {
            FailRun();
            await sync!;
         }
         else
         {
            string name = _runNames[random.Next(_runNames.Count)];
            SyncStatus next = _rows[name].Status switch
            {
               SyncStatus.Queued => random.Next(2) == 0 ? SyncStatus.Cloning : SyncStatus.Pulling,
               SyncStatus.Cloning or SyncStatus.Pulling => random.Next(10) switch
               {
                  < 6 => SyncStatus.Done,
                  < 9 => SyncStatus.Failed,
                  _ => SyncStatus.Canceled,
               },
               SyncStatus status => status, // final: the engine reports nothing more for it
            };
            if(next != _rows[name].Status)
            {
               Report(name, next);
            }
         }

         string[] table = Table(vm);
         Assert.True(
             Expected(vm.Filter).SequenceEqual(table, StringComparer.Ordinal),
             $"seed {seed}, step {step}, filter {vm.Filter}, running {_run is not null}:\n  expected {string.Join(", ", Expected(vm.Filter))}\n  table    {string.Join(", ", table)}");
      }
   }
}
