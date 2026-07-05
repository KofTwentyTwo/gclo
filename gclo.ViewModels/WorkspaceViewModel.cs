using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using gclo.Engine;

namespace gclo.ViewModels;

/// <summary>
/// Drives one workspace of the org-sync UI in two phases: <see cref="LoadReposCommand"/>
/// fills the repository table, then <see cref="SyncCommand"/> clones or updates the
/// selected subset with live per-repo progress. <see cref="RetryFailedCommand"/> re-runs
/// just the failures. A workspace is either backed by a saved <see cref="Account"/>
/// (seeded from its profile, with each finished sync recorded back to the
/// <see cref="AccountsStore"/>) or the ad-hoc Quick Sync mode when constructed without one.
/// </summary>
public sealed partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly IRepositoryLister _lister;
    private readonly IGitClient _git;
    private readonly IOrganizationLister _orgLister;
    private readonly Func<Action<RepoProgress>, IProgress<RepoProgress>> _progressFactory;
    private readonly TimeSpan _orgLookupDebounce;
    private readonly IActivityLog _log;
    private readonly Account? _account;
    private readonly AccountsStore? _accountsStore;
    private readonly Dictionary<string, RepoItemViewModel> _itemsByName = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _orgLoadCts;

    /// <summary>Canceled on dispose so an in-flight path recovery dies with the workspace.</summary>
    private readonly CancellationTokenSource _lifetimeCts = new();

    /// <summary>Breaks the AllSelected &lt;-&gt; item.IsSelected feedback loop while one side updates the other.</summary>
    private bool _syncingSelection;

    /// <summary>First repository name from the last load; makes <see cref="TargetPreview"/> concrete.</summary>
    private string? _sampleRepoName;

    /// <summary>
    /// Rows captured for the in-flight (or most recent) sync run. Progress is scoped to
    /// this set (#22): <see cref="TotalCount"/>/<see cref="CompletedCount"/> describe the
    /// run, not the whole table. Empty until the first run and reset by each load.
    /// </summary>
    private IReadOnlyList<RepoItemViewModel> _runSet = [];

    /// <summary>
    /// Production dependencies by default; pass fakes for testing. The default progress
    /// factory is <see cref="Progress{T}"/>, which marshals via the SynchronizationContext
    /// captured at construction (the UI thread in the app); tests inject a synchronous one.
    /// A non-null <paramref name="account"/> seeds the workspace from that profile — its
    /// organization, target folder, subfolder preference, and concurrency — and pulls the
    /// token from <paramref name="tokenVault"/> (the Token setter's org lookup fires
    /// naturally); finished syncs are then recorded on <paramref name="accountsStore"/>.
    /// </summary>
    public WorkspaceViewModel(
        IRepositoryLister? lister = null,
        IGitClient? git = null,
        IOrganizationLister? orgLister = null,
        Func<Action<RepoProgress>, IProgress<RepoProgress>>? progressFactory = null,
        TimeSpan? orgLookupDebounce = null,
        IActivityLog? log = null,
        Account? account = null,
        ITokenVault? tokenVault = null,
        AccountsStore? accountsStore = null)
    {
        _lister = lister ?? new GitHubRepositoryLister();
        _git = git ?? new LibGit2GitClient();
        _orgLister = orgLister ?? new GitHubOrganizationLister();
        _progressFactory = progressFactory ?? (handler => new Progress<RepoProgress>(handler));
        _orgLookupDebounce = orgLookupDebounce ?? TimeSpan.FromMilliseconds(600);
        _log = log ?? new FileActivityLog();
        _account = account;
        _accountsStore = accountsStore;
        Organization = "";
        Token = "";
        TargetFolder = "";
        NameFilter = "";
        BranchFilter = "";
        MaxConcurrency = AppSettings.DefaultConcurrency;
        StatusText = "";
        ResultMessage = "";
        AllSelected = true;
        CanEditInputs = true;

        if (account is not null)
        {
            Organization = account.Organization;
            TargetFolder = account.TargetRoot;
            CreateOrgSubfolder = account.CreateOrgSubfolder;
            MaxConcurrency = account.MaxConcurrency;
            // The Token setter's existing org lookup fires naturally with the vault token.
            Token = tokenVault?.TryRetrieve(account.Id) ?? "";
        }
    }

    /// <summary>Id of the account this workspace was created for, or null for Quick Sync.</summary>
    public Guid? AccountId => _account?.Id;

    /// <summary>Name shown for this workspace in navigation: the account's name, or "Quick Sync".</summary>
    public string DisplayName => _account?.Name ?? "Quick Sync";

    public ObservableCollection<RepoItemViewModel> Repos { get; } = new();

    /// <summary>
    /// The rows the table shows: the subset of <see cref="Repos"/> matching
    /// <see cref="Filter"/>, in the table's current sort order. Rebuilt when the filter
    /// changes, a load completes, a run starts or ends, and on terminal row transitions.
    /// </summary>
    public ObservableCollection<RepoItemViewModel> FilteredRepos { get; } = new();

    /// <summary>
    /// Rows with a git operation in flight right now; feeds the pinned active strip.
    /// Maintained incrementally from progress reports, so its size is bounded by
    /// <see cref="MaxConcurrency"/>. Cleared when a run starts and again when it ends.
    /// </summary>
    public ObservableCollection<RepoItemViewModel> ActiveRepos { get; } = new();

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
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedCommand))]
    public partial bool IsLoadingRepos { get; set; }

    /// <summary>
    /// True while a path-recovery checkout runs. Recovery writes a repository's working
    /// tree outside a sync run, so every other git entry point is locked out for its
    /// duration — two concurrent operations on one directory corrupt it.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadReposCommand))]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResolvePathsCommand))]
    public partial bool IsResolvingPaths { get; set; }

    /// <summary>When set, repositories are placed under TargetFolder\Organization.</summary>
    [ObservableProperty]
    public partial bool CreateOrgSubfolder { get; set; }

    /// <summary>Header checkbox state: setting it checks or unchecks every row.</summary>
    [ObservableProperty]
    public partial bool AllSelected { get; set; }

    /// <summary>True once a sync run has finished (successfully, canceled, or faulted).</summary>
    [ObservableProperty]
    public partial bool HasCompletedRun { get; set; }

    /// <summary>True while any row is in the Failed state; drives the navigation badge.</summary>
    [ObservableProperty]
    public partial bool HasFailedRepos { get; set; }

    /// <summary>Column the table is currently sorted by, or null for load order.</summary>
    [ObservableProperty]
    public partial string? SortColumn { get; set; }

    [ObservableProperty]
    public partial bool SortDescending { get; set; }

    /// <summary>Which rows the repository table shows; defaults to <see cref="RepoFilter.All"/>.</summary>
    [ObservableProperty]
    public partial RepoFilter Filter { get; set; }

    /// <summary>
    /// Column filter: case-insensitive substring the repository name must contain.
    /// Empty (the default) matches everything. Composes with <see cref="Filter"/>
    /// and the other column filters; selection state is unaffected — hidden rows
    /// stay selected and still sync.
    /// </summary>
    [ObservableProperty]
    public partial string NameFilter { get; set; }

    /// <summary>Column filter: case-insensitive substring the branch name must contain.</summary>
    [ObservableProperty]
    public partial string BranchFilter { get; set; }

    /// <summary>Column filter: null shows all, true only archived, false only unarchived.</summary>
    [ObservableProperty]
    public partial bool? ArchivedFilter { get; set; }

    /// <summary>Number of rows currently selected for the next sync.</summary>
    [ObservableProperty]
    public partial int SelectedCount { get; set; }

    /// <summary>Caption for the primary sync button, pluralized for the current selection.</summary>
    public string SyncButtonLabel => SelectedCount == 1 ? "Sync 1 repo" : $"Sync {SelectedCount} repos";

    /// <summary>True when the connect inputs may be edited: no load and no run in flight.</summary>
    [ObservableProperty]
    public partial bool CanEditInputs { get; set; }

    /// <summary>
    /// True once this workspace has completed its first successful repository load;
    /// drives the connect-card vs workspace visibility switch. Never reset.
    /// </summary>
    [ObservableProperty]
    public partial bool HasLoadedRepos { get; set; }

    /// <summary>Outcome of the most recent run; maps to the results InfoBar's severity.</summary>
    [ObservableProperty]
    public partial RunResultKind ResultKind { get; set; }

    /// <summary>Summary or error line of the most recent run, shown in the results InfoBar.</summary>
    [ObservableProperty]
    public partial string ResultMessage { get; set; }

    /// <summary>
    /// True while the results InfoBar is showing: set when a run ends, cleared when the
    /// next run or load starts. The page two-way binds it so the user can dismiss the bar.
    /// </summary>
    [ObservableProperty]
    public partial bool ResultOpen { get; set; }

    partial void OnFilterChanged(RepoFilter value) => RebuildFilteredRepos();

    partial void OnNameFilterChanged(string value) => RebuildFilteredRepos();

    partial void OnBranchFilterChanged(string value) => RebuildFilteredRepos();

    partial void OnArchivedFilterChanged(bool? value) => RebuildFilteredRepos();

    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(SyncButtonLabel));

    partial void OnIsRunningChanged(bool value) => UpdateCanEditInputs();

    partial void OnIsLoadingReposChanged(bool value) => UpdateCanEditInputs();

    partial void OnIsResolvingPathsChanged(bool value) => UpdateCanEditInputs();

    private void UpdateCanEditInputs()
        => CanEditInputs = !IsRunning && !IsLoadingRepos && !IsResolvingPaths;

    /// <summary>Clears the previous run's result surface; called when a new run or load starts.</summary>
    private void ResetRunResult()
    {
        ResultOpen = false;
        ResultKind = RunResultKind.None;
        ResultMessage = "";
    }

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

    /// <summary>Stops the in-flight org lookup and path recovery, if any.</summary>
    public void Dispose()
    {
        _orgLoadCts?.Cancel();
        _orgLoadCts?.Dispose();
        _orgLoadCts = null;
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
    }

    /// <summary>Debounced: each token edit cancels the previous lookup.</summary>
    private async Task RefreshOrganizationsAsync()
    {
        _orgLoadCts?.Cancel();
        _orgLoadCts?.Dispose();
        var cts = _orgLoadCts = new CancellationTokenSource();
        // Captured before any successor can dispose cts; used everywhere below.
        CancellationToken lookupToken = cts.Token;

        string token = Token.Trim();
        if (token.Length < 10)
        {
            Organizations.Clear();
            // The newest invocation owns the flag: a canceled predecessor deliberately
            // leaves it alone, so an early return must clear any spinner it left behind.
            IsLoadingOrgs = false;
            return; // not plausibly a complete PAT yet
        }

        try
        {
            await Task.Delay(_orgLookupDebounce, lookupToken); // debounce keystrokes / rapid pastes
            IsLoadingOrgs = true;
            var orgs = await _orgLister.ListOrganizationsAsync(token, lookupToken);
            lookupToken.ThrowIfCancellationRequested();

            // An editable ComboBox resets its Text when its ItemsSource is mutated,
            // and the TwoWay binding would wipe a value that was already set — an
            // account workspace seeds Organization in the constructor, and this
            // refresh lands ~a second later. Capture, restore, and force a binding
            // resync so the seeded (or typed) organization survives the refresh.
            string organizationBeforeRefresh = Organization;
            Organizations.Clear();
            foreach (string org in orgs)
            {
                Organizations.Add(org);
            }
            Organization = organizationBeforeRefresh;
            OnPropertyChanged(nameof(Organization));
            // The production lister always lists the token's own account first, so a
            // single entry means no organizations were visible.
            StatusText = orgs.Count <= 1
                ? "Only your personal account is visible — add read:org (classic) for organizations, or type an org name manually."
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
            int clamped = double.IsNaN(value)
                ? AppSettings.DefaultConcurrency
                : (int)Math.Clamp(Math.Round(value), AppSettings.MinConcurrency, AppSettings.MaxConcurrency);
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
        RecomputeSelectedCount();
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
            // A header push recomputes once after its loop instead of per row.
            RecomputeSelectedCount();
            UpdateAllSelectedFromItems();
        }
        SyncCommand.NotifyCanExecuteChanged();
    }

    private void RecomputeSelectedCount() => SelectedCount = Repos.Count(r => r.IsSelected);

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
        && !IsResolvingPaths
        && !string.IsNullOrWhiteSpace(Organization)
        && !string.IsNullOrWhiteSpace(Token);

    /// <summary>Phase one: lists the organization's repositories and fills the table, all selected.</summary>
    [RelayCommand(CanExecute = nameof(CanLoadRepos))]
    private async Task LoadReposAsync()
    {
        IsLoadingRepos = true;
        ResetRunResult();
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
            _runSet = []; // progress is table-scoped again until the next run
            SortColumn = null;
            SortDescending = false;
            _sampleRepoName = Repos.Count > 0 ? Repos[0].Name : null;
            UpdateAllSelectedFromItems();
            HasLoadedRepos = true;
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
            RecomputeSelectedCount();
            RebuildFilteredRepos();
            SyncCommand.NotifyCanExecuteChanged();
            RetryFailedCommand.NotifyCanExecuteChanged();
            RecomputeHasFailedRepos();
        }
    }

    // ---------------------------------------------------------------- sync

    private bool CanSync() =>
        !IsRunning
        && !IsLoadingRepos
        && !IsResolvingPaths
        && !string.IsNullOrWhiteSpace(TargetFolder)
        && Repos.Any(r => r.IsSelected);

    /// <summary>Phase two: clones or updates the selected repositories.</summary>
    [RelayCommand(CanExecute = nameof(CanSync), IncludeCancelCommand = true)]
    private async Task SyncAsync(CancellationToken cancellationToken)
    {
        IsRunning = true;
        ResetRunResult();
        ActiveRepos.Clear();
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
            // Progress is run-scoped (#22): the bar and 'N of M' describe this run's
            // selection, not the whole table; TotalCount keeps the run size afterwards.
            _runSet = selected;
            TotalCount = selected.Count;
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

            string summaryText = (summary.WasCanceled ? "Canceled" : "Finished")
                + $": {summary.Cloned} cloned, {summary.Updated} updated, "
                + $"{summary.Failed} failed, {summary.Canceled} canceled of {summary.Total}.";
            StatusText = summaryText;
            _log.Info(summaryText);

            ResultKind = summary.WasCanceled ? RunResultKind.Canceled
                : summary.Failed > 0 ? RunResultKind.PartialFailure
                : RunResultKind.Success;
            ResultMessage = summaryText;

            if (_account is not null && _accountsStore is not null)
            {
                try
                {
                    _accountsStore.RecordSyncResult(_account.Id, DateTimeOffset.UtcNow, summaryText);
                }
                catch (Exception ex)
                {
                    // Bookkeeping only: failing to stamp the account must not turn a
                    // completed sync into an error.
                    _log.Error(
                        $"Failed to record the sync result for account '{_account.Name}': {ex.Message}", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Canceled";
            ResultKind = RunResultKind.Canceled;
            ResultMessage = "Canceled";
            _log.Info("Sync canceled.");
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            ResultKind = RunResultKind.Error;
            ResultMessage = ex.Message;
            _log.Error($"Sync failed: {ex.Message}", ex);
        }
        finally
        {
            HasCompletedRun = true;
            IsRunning = false;
            ActiveRepos.Clear();
            RebuildFilteredRepos();
            ResultOpen = true;
        }
    }

    // ---------------------------------------------------------------- retry

    private bool CanRetryFailed() =>
        !IsRunning
        && !IsLoadingRepos
        && !IsResolvingPaths
        && Repos.Any(r => r.Status == SyncStatus.Failed);

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
        => item is not null && item.HasPathIssue && !IsRunning && !IsResolvingPaths;

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
        IsResolvingPaths = true;
        try
        {
            _log.Info($"{item.Name}: applying path recovery "
                + $"({recovery.SegmentRenames.Count} renamed, {recovery.SkippedPaths.Count} skipped).");
            // Pulling shows the row's indeterminate bar while the checkout runs.
            item.Status = SyncStatus.Pulling;
            await _git.ApplyRecoveryAsync(path, recovery, _lifetimeCts.Token);

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
        finally
        {
            IsResolvingPaths = false;
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

        RebuildFilteredRepos(); // the table binds FilteredRepos, which mirrors Repos' order
    }

    /// <summary>OrderBy/OrderByDescending are both stable: equal keys keep their current order.</summary>
    private List<RepoItemViewModel> OrderRepos<TKey>(Func<RepoItemViewModel, TKey> key, IComparer<TKey> comparer)
        => SortDescending
            ? Repos.OrderByDescending(key, comparer).ToList()
            : Repos.OrderBy(key, comparer).ToList();

    // ---------------------------------------------------------------- progress

    /// <summary>
    /// Raised with a message that assistive technology should announce. Per-repo
    /// failures use this channel; run summaries land in <see cref="StatusText"/>,
    /// whose live region already announces changes. Raised on the UI thread.
    /// </summary>
    public event Action<string>? AnnouncementRequested;

    /// <summary>Applies one engine progress report to the table. Runs on the UI thread.</summary>
    private void HandleProgress(RepoProgress report)
    {
        if (!_itemsByName.TryGetValue(report.RepoName, out RepoItemViewModel? item))
        {
            return; // rows are created at load time; ignore anything unknown
        }

        SyncStatus statusBefore = item.Status;
        if (report.Status != SyncStatus.Queued)
        {
            item.Status = report.Status;
            item.Error = report.Error;
            item.Percent = report.Percent;
            item.InvalidPaths = report.InvalidPaths;
        }

        // Active-strip membership is maintained incrementally: a full rescan per report
        // would cost O(rows) on every progress tick. Contains/Remove stay cheap because
        // the collection is bounded by MaxConcurrency.
        bool isActive = item.Status is SyncStatus.Cloning or SyncStatus.Pulling;
        if (isActive)
        {
            if (!ActiveRepos.Contains(item))
            {
                ActiveRepos.Add(item);
            }
        }
        else
        {
            ActiveRepos.Remove(item);
        }

        if (report.Status == SyncStatus.Failed)
        {
            _log.Error($"{report.RepoName} failed: {report.Error}");
            // Failures never reach StatusText (which has a live region), so assistive
            // technology hears them only through this explicit channel.
            AnnouncementRequested?.Invoke($"{report.RepoName} failed. {report.Error}");
        }

        if (report.Status is SyncStatus.Done or SyncStatus.Failed or SyncStatus.Canceled)
        {
            RecomputeCompletedCount();
        }
        else if (statusBefore != item.Status && Filter is RepoFilter.Active or RepoFilter.Pending)
        {
            // Status-dependent filters must follow NON-terminal transitions too (a row
            // entering Cloning leaves Pending and joins Active immediately); terminal
            // ones already rebuild via RecomputeCompletedCount, and the rebuild's
            // sequence check keeps redundant calls cheap and scroll-stable.
            RebuildFilteredRepos();
        }
    }

    /// <summary>
    /// Recounts terminal rows within the captured run set (#22) — before the first run
    /// the set is empty and the count stays 0. Also the hook for everything that follows
    /// a terminal transition: the failed-rows flag and the filtered table.
    /// </summary>
    private void RecomputeCompletedCount()
    {
        int completed = 0;
        foreach (RepoItemViewModel repo in _runSet)
        {
            if (repo.Status is SyncStatus.Done or SyncStatus.Failed or SyncStatus.Canceled)
            {
                completed++;
            }
        }
        CompletedCount = completed;
        RecomputeHasFailedRepos();
        RebuildFilteredRepos();
    }

    private void RecomputeHasFailedRepos()
        => HasFailedRepos = Repos.Any(r => r.Status == SyncStatus.Failed);

    // ---------------------------------------------------------------- filtering

    /// <summary>
    /// Rebuilds <see cref="FilteredRepos"/> from <see cref="Repos"/>, preserving the
    /// table's current sort order. A no-op when the visible set is already correct, so
    /// list controls keep their scroll position across progress ticks.
    /// </summary>
    private void RebuildFilteredRepos()
    {
        List<RepoItemViewModel> desired = Repos.Where(MatchesFilter).ToList();
        if (desired.SequenceEqual(FilteredRepos))
        {
            return;
        }

        FilteredRepos.Clear();
        foreach (RepoItemViewModel repo in desired)
        {
            FilteredRepos.Add(repo);
        }
    }

    private bool MatchesFilter(RepoItemViewModel repo)
        => MatchesStatusFilter(repo)
            && (NameFilter.Length == 0
                || repo.Name.Contains(NameFilter.Trim(), StringComparison.OrdinalIgnoreCase))
            && (BranchFilter.Length == 0
                || repo.BranchText.Contains(BranchFilter.Trim(), StringComparison.OrdinalIgnoreCase))
            && (ArchivedFilter is not { } archived || repo.IsArchived == archived);

    private bool MatchesStatusFilter(RepoItemViewModel repo) => Filter switch
    {
        RepoFilter.Active => repo.Status is SyncStatus.Cloning or SyncStatus.Pulling,
        RepoFilter.Failed => repo.Status == SyncStatus.Failed,
        RepoFilter.Pending => repo.Status == SyncStatus.Queued,
        _ => true,
    };
}
