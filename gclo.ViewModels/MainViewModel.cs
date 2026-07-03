using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using gclo.Engine;

namespace gclo.ViewModels;

/// <summary>
/// Drives the org-sync UI in two phases: <see cref="LoadReposCommand"/> fills the repository
/// table, then <see cref="SyncCommand"/> clones or updates the selected subset with live
/// per-repo progress. <see cref="RetryFailedCommand"/> re-runs just the failures.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IRepositoryLister _lister;
    private readonly IGitClient _git;
    private readonly IOrganizationLister _orgLister;
    private readonly Func<Action<RepoProgress>, IProgress<RepoProgress>> _progressFactory;
    private readonly TimeSpan _orgLookupDebounce;
    private readonly IActivityLog _log;
    private readonly Dictionary<string, RepoItemViewModel> _itemsByName = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _orgLoadCts;

    /// <summary>Breaks the AllSelected &lt;-&gt; item.IsSelected feedback loop while one side updates the other.</summary>
    private bool _syncingSelection;

    /// <summary>First repository name from the last load; makes <see cref="TargetPreview"/> concrete.</summary>
    private string? _sampleRepoName;

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
        TimeSpan? orgLookupDebounce = null,
        IActivityLog? log = null)
    {
        _lister = lister ?? new GitHubRepositoryLister();
        _git = git ?? new LibGit2GitClient();
        _orgLister = orgLister ?? new GitHubOrganizationLister();
        _progressFactory = progressFactory ?? (handler => new Progress<RepoProgress>(handler));
        _orgLookupDebounce = orgLookupDebounce ?? TimeSpan.FromMilliseconds(600);
        _log = log ?? new FileActivityLog();
        Organization = "";
        Token = "";
        TargetFolder = "";
        MaxConcurrency = 8;
        StatusText = "";
        AllSelected = true;
    }

    public ObservableCollection<RepoItemViewModel> Repos { get; } = new();

    /// <summary>Organizations discovered from the current token; feeds the org dropdown.</summary>
    public ObservableCollection<string> Organizations { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadReposCommand))]
    public partial string Organization { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadReposCommand))]
    public partial string Token { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    public partial string TargetFolder { get; set; }

    [ObservableProperty]
    public partial int MaxConcurrency { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadReposCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResolvePathsCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial int CompletedCount { get; set; }

    [ObservableProperty]
    public partial int TotalCount { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingOrgs { get; set; }

    /// <summary>True while <see cref="LoadReposCommand"/> is listing repositories.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadReposCommand))]
    public partial bool IsLoadingRepos { get; set; }

    /// <summary>When set, repositories are placed under TargetFolder\Organization.</summary>
    [ObservableProperty]
    public partial bool CreateOrgSubfolder { get; set; }

    /// <summary>Header checkbox state: setting it checks or unchecks every row.</summary>
    [ObservableProperty]
    public partial bool AllSelected { get; set; }

    /// <summary>True once a sync run has finished (successfully, canceled, or faulted).</summary>
    [ObservableProperty]
    public partial bool HasCompletedRun { get; set; }

    /// <summary>Column the table is currently sorted by, or null for load order.</summary>
    [ObservableProperty]
    public partial string? SortColumn { get; set; }

    [ObservableProperty]
    public partial bool SortDescending { get; set; }

    /// <summary>Root folder repositories are placed under: TargetFolder, or TargetFolder\Organization.</summary>
    public string EffectiveTargetRoot
    {
        get
        {
            string folder = TargetFolder.Trim();
            if (folder.Length == 0)
            {
                return "";
            }
            string org = Organization.Trim();
            return CreateOrgSubfolder && org.Length > 0 ? Path.Combine(folder, org) : folder;
        }
    }

    /// <summary>Example final path for one repository, e.g. C:\src\acme\my-repo.</summary>
    public string TargetPreview
    {
        get
        {
            string root = EffectiveTargetRoot;
            return root.Length == 0 ? "" : Path.Combine(root, _sampleRepoName ?? "my-repo");
        }
    }

    partial void OnTargetFolderChanged(string value) => NotifyTargetPathChanged();

    partial void OnOrganizationChanged(string value) => NotifyTargetPathChanged();

    partial void OnCreateOrgSubfolderChanged(bool value) => NotifyTargetPathChanged();

    private void NotifyTargetPathChanged()
    {
        OnPropertyChanged(nameof(EffectiveTargetRoot));
        OnPropertyChanged(nameof(TargetPreview));
    }

    // Runs on the UI thread (Token is only set from UI handlers), so the async
    // continuations below stay on the UI thread and may touch Organizations directly.
    partial void OnTokenChanged(string value) => _ = RefreshOrganizationsAsync();

    /// <summary>Stops and releases the in-flight org lookup, if any.</summary>
    public void Dispose()
    {
        _orgLoadCts?.Cancel();
        _orgLoadCts?.Dispose();
        _orgLoadCts = null;
    }

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

    // ---------------------------------------------------------------- selection

    /// <summary>Applies a header-checkbox change to every row.</summary>
    partial void OnAllSelectedChanged(bool value)
    {
        if (_syncingSelection)
        {
            return; // being recomputed from an item change; do not push back down
        }

        _syncingSelection = true;
        try
        {
            foreach (RepoItemViewModel item in Repos)
            {
                item.IsSelected = value;
            }
        }
        finally
        {
            _syncingSelection = false;
        }
        SyncCommand.NotifyCanExecuteChanged();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RepoItemViewModel.HasPathIssue))
        {
            // Parameterized command: the row's 'Resolve...' button re-queries CanExecute.
            ResolvePathsCommand.NotifyCanExecuteChanged();
            return;
        }
        if (e.PropertyName != nameof(RepoItemViewModel.IsSelected))
        {
            return;
        }
        if (!_syncingSelection)
        {
            UpdateAllSelectedFromItems();
        }
        SyncCommand.NotifyCanExecuteChanged();
    }

    private void UpdateAllSelectedFromItems()
    {
        bool all = Repos.All(r => r.IsSelected);
        if (AllSelected != all)
        {
            _syncingSelection = true;
            try
            {
                AllSelected = all;
            }
            finally
            {
                _syncingSelection = false;
            }
        }
    }

    // ---------------------------------------------------------------- load

    private bool CanLoadRepos() =>
        !IsRunning
        && !IsLoadingRepos
        && !string.IsNullOrWhiteSpace(Organization)
        && !string.IsNullOrWhiteSpace(Token);

    /// <summary>Phase one: lists the organization's repositories and fills the table, all selected.</summary>
    [RelayCommand(CanExecute = nameof(CanLoadRepos))]
    private async Task LoadReposAsync()
    {
        IsLoadingRepos = true;
        try
        {
            string organization = Organization.Trim();
            StatusText = "Loading repositories...";
            _log.Info($"Loading repositories for organization '{organization}'.");

            var descriptors = (await _lister
                .ListOrganizationRepositoriesAsync(organization, Token.Trim()))
                // Names key the row dictionary; a duplicate (possible from pagination
                // shifts) must not produce two rows fighting over one folder.
                .DistinctBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (RepoItemViewModel existing in Repos)
            {
                existing.PropertyChanged -= OnItemPropertyChanged;
            }
            Repos.Clear();
            _itemsByName.Clear();

            foreach (RepoDescriptor descriptor in descriptors)
            {
                var item = new RepoItemViewModel(descriptor);
                item.PropertyChanged += OnItemPropertyChanged;
                _itemsByName[descriptor.Name] = item;
                Repos.Add(item);
            }

            TotalCount = Repos.Count;
            CompletedCount = 0;
            SortColumn = null;
            SortDescending = false;
            _sampleRepoName = Repos.Count > 0 ? Repos[0].Name : null;
            UpdateAllSelectedFromItems();
            StatusText = $"{Repos.Count} repositories loaded.";
            _log.Info($"Loaded {Repos.Count} repositories for organization '{organization}'.");
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            _log.Error($"Loading repositories failed: {ex.Message}", ex);
        }
        finally
        {
            IsLoadingRepos = false;
            NotifyTargetPathChanged();
            SyncCommand.NotifyCanExecuteChanged();
            RetryFailedCommand.NotifyCanExecuteChanged();
        }
    }

    // ---------------------------------------------------------------- sync

    private bool CanSync() =>
        !IsRunning
        && !string.IsNullOrWhiteSpace(TargetFolder)
        && Repos.Any(r => r.IsSelected);

    /// <summary>Phase two: clones or updates the selected repositories.</summary>
    [RelayCommand(CanExecute = nameof(CanSync), IncludeCancelCommand = true)]
    private async Task SyncAsync(CancellationToken cancellationToken)
    {
        IsRunning = true;
        try
        {
            var selected = Repos.Where(r => r.IsSelected).ToList();
            foreach (RepoItemViewModel item in selected)
            {
                item.Status = SyncStatus.Queued;
                item.Error = null;
                item.Percent = null;
                item.InvalidPaths = null;
            }
            RecomputeCompletedCount();

            string targetRoot = EffectiveTargetRoot;
            StatusText = $"Syncing {selected.Count} repositories...";
            _log.Info($"Sync started: {selected.Count} repositories into '{targetRoot}'.");

            // Constructed on the UI thread: Progress<T> captures the WinUI
            // SynchronizationContext, so HandleProgress always runs on the UI thread.
            var progress = _progressFactory(HandleProgress);

            var engine = new OrgSyncEngine(_lister, _git);
            var request = new SyncRequest(
                Organization.Trim(), Token.Trim(), targetRoot, MaxConcurrency);
            IReadOnlyList<RepoDescriptor> descriptors = selected.Select(r => r.Descriptor).ToList();

            SyncSummary summary = await engine.SyncAsync(request, descriptors, progress, cancellationToken);

            StatusText = summary.WasCanceled
                ? $"Canceled: {summary.Cloned} cloned, {summary.Updated} updated, {summary.Failed} failed, {summary.Canceled} canceled of {summary.Total}."
                : $"Finished: {summary.Cloned} cloned, {summary.Updated} updated, {summary.Failed} failed, {summary.Canceled} canceled of {summary.Total}.";
            _log.Info($"Sync finished: {summary.Cloned} cloned, {summary.Updated} updated, "
                + $"{summary.Failed} failed, {summary.Canceled} canceled of {summary.Total}.");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Canceled";
            _log.Info("Sync canceled.");
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            _log.Error($"Sync failed: {ex.Message}", ex);
        }
        finally
        {
            HasCompletedRun = true;
            IsRunning = false;
        }
    }

    // ---------------------------------------------------------------- retry

    private bool CanRetryFailed() =>
        !IsRunning && Repos.Any(r => r.Status == SyncStatus.Failed);

    /// <summary>Selects exactly the failed repositories and runs the same sync path again.</summary>
    [RelayCommand(CanExecute = nameof(CanRetryFailed))]
    private async Task RetryFailedAsync()
    {
        foreach (RepoItemViewModel item in Repos)
        {
            item.IsSelected = item.Status == SyncStatus.Failed;
        }
        // Executing SyncCommand itself keeps SyncCancelCommand working for retry runs.
        await SyncCommand.ExecuteAsync(null);
    }

    // ---------------------------------------------------------------- path recovery

    /// <summary>
    /// Raised when the user asks to resolve a row's Windows-invalid paths. The view
    /// subscribes and shows recovery UI (the VM stays UI-free), returning the user's
    /// decision, or null when they cancel. With no subscriber the command does nothing.
    /// </summary>
    public Func<RepoItemViewModel, Task<PathRecovery?>>? RecoveryInteraction { get; set; }

    private bool CanResolvePaths(RepoItemViewModel item)
        => item is not null && item.HasPathIssue && !IsRunning;

    /// <summary>
    /// Asks the view (via <see cref="RecoveryInteraction"/>) how to rename or skip the
    /// row's invalid paths, then applies that recovery and checks the repository out.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanResolvePaths))]
    private async Task ResolvePathsAsync(RepoItemViewModel item)
    {
        if (item is null || RecoveryInteraction is not { } interaction)
        {
            return;
        }

        PathRecovery? recovery = await interaction(item);
        if (recovery is null)
        {
            return; // user canceled; the row keeps its Failed state and payload
        }

        string path = Path.Combine(EffectiveTargetRoot, item.Name);
        try
        {
            _log.Info($"{item.Name}: applying path recovery "
                + $"({recovery.SegmentRenames.Count} renamed, {recovery.SkippedPaths.Count} skipped).");
            await _git.ApplyRecoveryAsync(path, Token.Trim(), recovery, CancellationToken.None);

            item.Status = SyncStatus.Done;
            item.Error = null;
            item.InvalidPaths = null;
            _log.Info($"{item.Name}: path recovery applied; repository checked out.");
        }
        catch (Exception ex)
        {
            item.Status = SyncStatus.Failed;
            item.Error = ex.Message;
            // A still-invalid or colliding mapping keeps the row resolvable with the
            // fresh path list; any other failure clears the payload (renaming again
            // would not help).
            item.InvalidPaths = (ex as InvalidRepositoryPathsException)?.Paths;
            _log.Error($"{item.Name}: path recovery failed: {ex.Message}", ex);
        }
        RecomputeCompletedCount();
        RetryFailedCommand.NotifyCanExecuteChanged();
    }

    // ---------------------------------------------------------------- sorting

    /// <summary>
    /// Sorts the table by a column ("Name", "Status", "Branch", or "Archived"); a second
    /// click on the same column flips the direction. The re-order is stable and in place.
    /// </summary>
    [RelayCommand]
    private void Sort(string column)
    {
        if (column is not ("Name" or "Status" or "Branch" or "Archived"))
        {
            return;
        }

        if (SortColumn == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = false;
        }

        List<RepoItemViewModel> sorted = column switch
        {
            "Name" => OrderRepos(r => r.Name, StringComparer.OrdinalIgnoreCase),
            "Status" => OrderRepos(r => r.Status, Comparer<SyncStatus>.Default),
            "Branch" => OrderRepos(r => r.BranchText, StringComparer.OrdinalIgnoreCase),
            _ => OrderRepos(r => r.IsArchived, Comparer<bool>.Default),
        };

        // Move (rather than clear + re-add) so list controls keep row containers stable.
        for (int target = 0; target < sorted.Count; target++)
        {
            int current = Repos.IndexOf(sorted[target]);
            if (current != target)
            {
                Repos.Move(current, target);
            }
        }
    }

    /// <summary>OrderBy/OrderByDescending are both stable: equal keys keep their current order.</summary>
    private List<RepoItemViewModel> OrderRepos<TKey>(Func<RepoItemViewModel, TKey> key, IComparer<TKey> comparer)
        => SortDescending
            ? Repos.OrderByDescending(key, comparer).ToList()
            : Repos.OrderBy(key, comparer).ToList();

    // ---------------------------------------------------------------- progress

    /// <summary>Applies one engine progress report to the table. Runs on the UI thread.</summary>
    private void HandleProgress(RepoProgress report)
    {
        if (!_itemsByName.TryGetValue(report.RepoName, out RepoItemViewModel? item))
        {
            return; // rows are created at load time; ignore anything unknown
        }

        if (report.Status != SyncStatus.Queued)
        {
            item.Status = report.Status;
            item.Error = report.Error;
            item.Percent = report.Percent;
            item.InvalidPaths = report.InvalidPaths;
        }

        if (report.Status == SyncStatus.Failed)
        {
            _log.Error($"{report.RepoName} failed: {report.Error}");
        }

        if (report.Status is SyncStatus.Done or SyncStatus.Failed or SyncStatus.Canceled)
        {
            RecomputeCompletedCount();
        }
    }

    private void RecomputeCompletedCount()
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
