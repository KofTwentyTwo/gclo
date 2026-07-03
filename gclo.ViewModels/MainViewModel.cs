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
    private readonly IOrganizationLister _orgLister;
    private readonly Func<Action<RepoProgress>, IProgress<RepoProgress>> _progressFactory;
    private readonly TimeSpan _orgLookupDebounce;
    private readonly Dictionary<string, RepoItemViewModel> _itemsByName = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _orgLoadCts;

    /// <summary>
    /// Production dependencies by default; pass fakes for testing. The default progress
    /// factory is <see cref="Progress{T}"/>, which marshals via the SynchronizationContext
    /// captured at construction (the UI thread in the app); tests inject a synchronous one.
    /// </summary>
    public MainViewModel(
        IRepositoryLister? lister = null,
        IGitClient? git = null,
        IOrganizationLister? orgLister = null,
        Func<Action<RepoProgress>, IProgress<RepoProgress>>? progressFactory = null,
        TimeSpan? orgLookupDebounce = null)
    {
        _lister = lister ?? new GitHubRepositoryLister();
        _git = git ?? new LibGit2GitClient();
        _orgLister = orgLister ?? new GitHubOrganizationLister();
        _progressFactory = progressFactory ?? (handler => new Progress<RepoProgress>(handler));
        _orgLookupDebounce = orgLookupDebounce ?? TimeSpan.FromMilliseconds(600);
        Organization = "";
        Token = "";
        TargetFolder = "";
        MaxConcurrency = 8;
        StatusText = "";
    }

    public ObservableCollection<RepoItemViewModel> Repos { get; } = new();

    /// <summary>Organizations discovered from the current token; feeds the org dropdown.</summary>
    public ObservableCollection<string> Organizations { get; } = new();

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

    [ObservableProperty]
    public partial bool IsLoadingOrgs { get; set; }

    // Runs on the UI thread (Token is only set from UI handlers), so the async
    // continuations below stay on the UI thread and may touch Organizations directly.
    partial void OnTokenChanged(string value) => _ = RefreshOrganizationsAsync();

    /// <summary>Debounced: each token edit cancels the previous lookup.</summary>
    private async Task RefreshOrganizationsAsync()
    {
        _orgLoadCts?.Cancel();
        var cts = _orgLoadCts = new CancellationTokenSource();

        string token = Token.Trim();
        if (token.Length < 10)
        {
            Organizations.Clear();
            return; // not plausibly a complete PAT yet
        }

        try
        {
            await Task.Delay(_orgLookupDebounce, cts.Token); // debounce keystrokes / rapid pastes
            IsLoadingOrgs = true;
            var orgs = await _orgLister.ListOrganizationsAsync(token, cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            Organizations.Clear();
            foreach (string org in orgs)
            {
                Organizations.Add(org);
            }
            StatusText = orgs.Count == 0
                ? "Token accepted, but it cannot list organizations (fine-grained PAT or missing read:org?). Type an organization or account name manually."
                : $"Found {orgs.Count} organizations and accounts.";
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer token edit
        }
        catch (Exception ex)
        {
            if (_orgLoadCts == cts)
            {
                StatusText = ex.Message;
            }
        }
        finally
        {
            if (_orgLoadCts == cts)
            {
                IsLoadingOrgs = false;
            }
        }
    }

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
            var progress = _progressFactory(HandleProgress);

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
