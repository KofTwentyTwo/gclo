using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using gclo.Engine;

namespace gclo.ViewModels;

/// <summary>Drives the org-sync UI: inputs, the Sync/Cancel commands, and live per-repo progress.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IRepositoryLister _lister;
    private readonly IGitClient _git;
    private readonly Dictionary<string, RepoItemViewModel> _itemsByName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Production dependencies by default; pass fakes for testing.</summary>
    public MainViewModel(IRepositoryLister? lister = null, IGitClient? git = null)
    {
        _lister = lister ?? new GitHubRepositoryLister();
        _git = git ?? new LibGit2GitClient();
        Organization = "";
        Token = "";
        TargetFolder = "";
        MaxConcurrency = 8;
        StatusText = "";
    }

    public ObservableCollection<RepoItemViewModel> Repos { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    public partial string Organization { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    public partial string Token { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    public partial string TargetFolder { get; set; }

    [ObservableProperty]
    public partial int MaxConcurrency { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial int CompletedCount { get; set; }

    [ObservableProperty]
    public partial int TotalCount { get; set; }

    /// <summary>
    /// Double-typed view of <see cref="MaxConcurrency"/> for NumberBox.Value, which binds a double.
    /// </summary>
    public double MaxConcurrencyValue
    {
        get => MaxConcurrency;
        set
        {
            int clamped = double.IsNaN(value) ? 8 : (int)Math.Clamp(Math.Round(value), 1, 64);
            if (MaxConcurrency != clamped)
            {
                MaxConcurrency = clamped; // OnMaxConcurrencyChanged notifies MaxConcurrencyValue too
            }
            else
            {
                // The int did not change (e.g. the box was cleared to NaN): push the
                // canonical value back so the control redisplays it.
                OnPropertyChanged(nameof(MaxConcurrencyValue));
            }
        }
    }

    partial void OnMaxConcurrencyChanged(int value) => OnPropertyChanged(nameof(MaxConcurrencyValue));

    private bool CanSync() =>
        !IsRunning
        && !string.IsNullOrWhiteSpace(Organization)
        && !string.IsNullOrWhiteSpace(Token)
        && !string.IsNullOrWhiteSpace(TargetFolder);

    [RelayCommand(CanExecute = nameof(CanSync), IncludeCancelCommand = true)]
    private async Task SyncAsync(CancellationToken cancellationToken)
    {
        IsRunning = true;
        try
        {
            Repos.Clear();
            _itemsByName.Clear();
            CompletedCount = 0;
            TotalCount = 0;
            StatusText = "Listing repositories...";

            // Constructed on the UI thread: Progress<T> captures the WinUI
            // SynchronizationContext, so HandleProgress always runs on the UI thread.
            var progress = new Progress<RepoProgress>(HandleProgress);

            var engine = new OrgSyncEngine(_lister, _git);
            var request = new SyncRequest(
                Organization.Trim(), Token.Trim(), TargetFolder.Trim(), MaxConcurrency);

            SyncSummary summary = await engine.SyncAsync(request, progress, cancellationToken);

            StatusText = summary.WasCanceled
                ? $"Canceled: {summary.Cloned} cloned, {summary.Updated} updated, {summary.Failed} failed, {summary.Canceled} canceled of {summary.Total}."
                : $"Finished: {summary.Cloned} cloned, {summary.Updated} updated, {summary.Failed} failed, {summary.Canceled} canceled of {summary.Total}.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Canceled";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Applies one engine progress report to the list. Runs on the UI thread.</summary>
    private void HandleProgress(RepoProgress report)
    {
        if (!_itemsByName.TryGetValue(report.RepoName, out RepoItemViewModel? item))
        {
            item = new RepoItemViewModel(report.RepoName);
            _itemsByName[report.RepoName] = item;
            Repos.Add(item);
            TotalCount = Repos.Count;
        }

        if (report.Status != SyncStatus.Queued)
        {
            item.Status = report.Status;
            item.Error = report.Error;
            item.Percent = report.Percent;
        }

        if (report.Status is SyncStatus.Done or SyncStatus.Failed or SyncStatus.Canceled)
        {
            int completed = 0;
            foreach (RepoItemViewModel repo in Repos)
            {
                if (repo.Status is SyncStatus.Done or SyncStatus.Failed or SyncStatus.Canceled)
                {
                    completed++;
                }
            }
            CompletedCount = completed;
        }
    }
}
