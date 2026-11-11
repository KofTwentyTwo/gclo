/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

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

   private readonly IWslCloner _wsl;

   /// <summary>Result of the one-time WSL probe; null until the first path recovery asks for it.</summary>
   private WslAvailability? _wslAvailability;

   private readonly IOrganizationLister _orgLister;

   private readonly Func<Action<RepoProgress>, IProgress<RepoProgress>> _progressFactory;

   private readonly TimeSpan _orgLookupDebounce;

   private readonly IActivityLog _log;

   private readonly Account? _account;

   /// <summary>Where an account workspace's token comes from; null for Quick Sync.</summary>
   private readonly AccountTokenResolver? _tokenResolver;

   /// <summary>True while the token is being taken from its source again, which is not the user typing one.</summary>
   private bool _readingTokenFromItsSource;

   private readonly AccountsStore? _accountsStore;

   private readonly Dictionary<string, RepoItemViewModel> _itemsByName = new(StringComparer.OrdinalIgnoreCase);

   private CancellationTokenSource? _orgLoadCts;

   /// <summary>
   /// Position of every row in <see cref="Repos"/>; rebuilt on load and sort. Lets
   /// progress-driven membership changes find a row's slot in <see cref="FilteredRepos"/>
   /// by binary search instead of rebuilding the whole bound list (#30).
   /// </summary>
   private readonly Dictionary<RepoItemViewModel, int> _reposIndex = new();

   /// <summary>Mirror of <see cref="FilteredRepos"/> for O(1) membership checks.</summary>
   private readonly HashSet<RepoItemViewModel> _filteredSet = new();

   /// <summary>Canceled on dispose so an in-flight path recovery dies with the workspace.</summary>
   private readonly CancellationTokenSource _lifetimeCts = new();

   /// <summary>
   /// Runs one sync pass; the real <see cref="OrgSyncEngine"/> by default. A test seam:
   /// the engine swallows cancellation into a summary, so the only way to exercise the
   /// view model's own <see cref="OperationCanceledException"/> handling is to inject a
   /// runner that throws it.
   /// </summary>
   internal Func<SyncRequest, IReadOnlyList<RepoDescriptor>, IProgress<RepoProgress>, CancellationToken, Task<SyncSummary>>? SyncRunner
   {
      get;
      set;
   }

   /// <summary>Counts the starts and finishes of rows; see <see cref="RepoItemViewModel.LastActivity"/>.</summary>
   private long _activityClock;

   /// <summary>Breaks the AllSelected &lt;-&gt; item.IsSelected feedback loop while one side updates the other.</summary>
   private bool _syncingSelection;

   /// <summary>False until the constructor finished seeding properties; gates user-action logging.</summary>
   private readonly bool _constructed;

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
   /// factory is <see cref="BatchedRepoProgress"/>, which delivers on the
   /// SynchronizationContext it is created on (the UI thread in the app), a few times a
   /// second rather than once per report; tests inject a synchronous one.
   /// A non-null <paramref name="account"/> seeds the workspace from that profile — its
   /// organization, target folder, subfolder preference, and concurrency — and pulls the
   /// token from <paramref name="tokenVault"/> (the Token setter's org lookup fires
   /// naturally); finished syncs are then recorded on <paramref name="accountsStore"/>.
   /// The token is read from the vault again whenever it is about to be used
   /// (<see cref="RefreshAccountToken"/>), so one that was replaced or removed since
   /// (the default token in Settings, the account's own in the wizard) takes effect
   /// without a restart.
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
       AccountsStore? accountsStore = null,
       IWslCloner? wsl = null)
   {
      // Without listers of its own, a workspace gets those of its account's forge: Quick Sync is GitHub.
      _lister = lister ?? (account is null ? new GitHubRepositoryLister() : AccountListers.RepositoryListerFor(account));
      _git = git ?? new LibGit2GitClient();
      _wsl = wsl ?? new WslCloner();
      _orgLister = orgLister ?? (account is null ? new GitHubOrganizationLister() : AccountListers.OrganizationListerFor(account));
      _progressFactory = progressFactory ?? DefaultProgress;
      _orgLookupDebounce = orgLookupDebounce ?? TimeSpan.FromMilliseconds(600);
      _log = log ?? new FileActivityLog(GcloPaths.LogDirectory);
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
      LoadErrorTitle = "";
      LoadErrorMessage = "";
      AllSelected = true;
      CanEditInputs = true;

      if(account is not null)
      {
         Organization = account.Organization;
         TargetFolder = account.TargetRoot;
         CreateOrgSubfolder = account.CreateOrgSubfolder;
         MaxConcurrency = account.MaxConcurrency;
         // The Token setter's existing org lookup fires naturally with the resolved
         // token: the account's own entry, or the default token from Settings.
         _tokenResolver = tokenVault is null ? null : new AccountTokenResolver(tokenVault);
         Token = _tokenResolver?.TryResolve(account) ?? "";
      }
      _constructed = true;
   }



   /// <summary>Id of the account this workspace was created for, or null for Quick Sync.</summary>
   public Guid? AccountId => _account?.Id;



   /// <summary>Name shown for this workspace in navigation: the account's name, or "Quick Sync".</summary>
   public string DisplayName => _account?.Name ?? "Quick Sync";

   /// <summary>
   /// True for an account workspace whose token is the default token from Settings
   /// (<see cref="TokenSource.Default"/>): the pane tags it, and Sync All names
   /// Settings when that token is missing.
   /// </summary>
   public bool UsesDefaultToken => _account?.UsesDefaultToken == true;



   /// <summary>
   /// Called by the shell when the workspace is shown. A saved account knows its
   /// organization, folder and token, so it loads its repositories straight away
   /// instead of landing on the connect card; Quick Sync, a workspace that already
   /// loaded, or an account whose token is missing (the card shows why) do nothing.
   /// Returns true when a load was started.
   /// </summary>
   public bool LoadRepositoriesIfSaved()
   {
      RefreshAccountToken();
      if(_account is null || HasLoadedRepos || !LoadReposCommand.CanExecute(null))
      {
         return false;
      }
      _log.Info($"Opening account '{DisplayName}': loading its repositories.");
      _ = LoadReposCommand.ExecuteAsync(null);
      return true;
   }



   public ObservableCollection<RepoItemViewModel> Repos { get; } = new();

   /// <summary>
   /// The rows the table shows: the subset of <see cref="Repos"/> matching
   /// <see cref="Filter"/>, in the table's current sort order. Rebuilt when the filter
   /// changes, a load completes, a run starts or ends, and on terminal row transitions.
   /// </summary>
   public ObservableCollection<RepoItemViewModel> FilteredRepos { get; } = new();

   /// <summary>Organizations discovered from the current token; feeds the org dropdown.</summary>
   public ObservableCollection<string> Organizations { get; } = new();

   [ObservableProperty]
   [NotifyCanExecuteChangedFor(nameof(LoadReposCommand))]
   public partial string Organization { get; set; }

   [ObservableProperty]
   [NotifyCanExecuteChangedFor(nameof(LoadReposCommand))]
   [NotifyPropertyChangedFor(nameof(MissingTokenDescription))]
   [NotifyPropertyChangedFor(nameof(MissingTokenAdvice))]
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

   /// <summary>
   /// Header checkbox state: setting it checks or unchecks every VISIBLE row (the rows
   /// in <see cref="FilteredRepos"/>), and it reads true when every visible row is
   /// selected. With no filter active that is every row; with a filter it makes
   /// "filter, then select what you see" the subset workflow (#30). Hidden rows keep
   /// their selection and still sync.
   /// </summary>
   [ObservableProperty]
   public partial bool AllSelected { get; set; }

   /// <summary>
   /// Organization whose repositories the table currently holds (trimmed, as loaded);
   /// null before the first successful load.
   /// </summary>
   [ObservableProperty]
   [NotifyPropertyChangedFor(nameof(IsTableStale))]
   [NotifyPropertyChangedFor(nameof(StaleTableMessage))]
   public partial string? LoadedOrganization { get; set; }



   /// <summary>
   /// True when <see cref="Organization"/> was edited after a load: the table still
   /// shows the previous organization's rows while the chip and target path describe
   /// the new one. Sync is blocked until a reload replaces the rows (#30).
   /// </summary>
   public bool IsTableStale
       => LoadedOrganization is not null
           && !string.Equals(Organization.Trim(), LoadedOrganization, StringComparison.OrdinalIgnoreCase);



   /// <summary>One-line explanation shown while <see cref="IsTableStale"/>; empty otherwise.</summary>
   public string StaleTableMessage
       => IsTableStale
           ? $"The table still shows '{LoadedOrganization}'. Reload to list '{Organization.Trim()}' before syncing."
           : "";



   /// <summary>Title of the error surface for a failed load or organization lookup.</summary>
   [ObservableProperty]
   public partial string LoadErrorTitle { get; set; }

   /// <summary>Message of the most recent failed load or organization lookup; empty when none.</summary>
   [ObservableProperty]
   public partial string LoadErrorMessage { get; set; }

   /// <summary>
   /// True while the load-error surface is showing. Set by a failed load or
   /// organization lookup, cleared when the next load starts or a lookup succeeds; the
   /// page two-way binds it so the user can dismiss the bar (#30).
   /// </summary>
   [ObservableProperty]
   public partial bool LoadErrorOpen { get; set; }

   /// <summary>True once a sync run has finished (successfully, canceled, or faulted).</summary>
   [ObservableProperty]
   public partial bool HasCompletedRun { get; set; }

   /// <summary>True while any row is in the Failed state; drives the navigation badge.</summary>
   [ObservableProperty]
   public partial bool HasFailedRepos { get; set; }

   /// <summary>
   /// Column the table is sorted by, or null for the default order: while a sync
   /// runs, the repositories in flight, then the failed ones, then the finished ones
   /// (the latest first), then the ones still waiting; otherwise the failed ones
   /// first, then by name (#168).
   /// </summary>
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
         if(folder.Length == 0)
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



   partial void OnOrganizationChanged(string value)
   {
      NotifyTargetPathChanged();
      OnPropertyChanged(nameof(IsTableStale));
      OnPropertyChanged(nameof(StaleTableMessage));
      SyncCommand.NotifyCanExecuteChanged();
   }



   /// <summary>Records a failed load or lookup on the error surface (and the live region).</summary>
   private void ShowLoadError(string title, string message)
   {
      StatusText = message;
      LoadErrorTitle = title;
      LoadErrorMessage = message;
      LoadErrorOpen = true;
   }



   partial void OnCreateOrgSubfolderChanged(bool value) => NotifyTargetPathChanged();



   private void NotifyTargetPathChanged()
   {
      OnPropertyChanged(nameof(EffectiveTargetRoot));
      OnPropertyChanged(nameof(TargetPreview));
   }



   // Runs on the UI thread (Token is only set from UI handlers), so the async
   // continuations below stay on the UI thread and may touch Organizations directly.
   partial void OnTokenChanged(string value)
   {
      // The value itself never reaches the log; its length is enough to tell a
      // paste from a cleared box when reading back a session (#40). Construction
      // seeds the property (empty, or the vault token) and is not a user action.
      if(_readingTokenFromItsSource)
      {
         _log.Info(value.Length == 0
             ? $"Workspace '{DisplayName}': its token is no longer saved."
             : $"Workspace '{DisplayName}': its saved token changed ({value.Length} characters).");
      }
      else if(_constructed)
      {
         _log.Info(value.Length == 0
             ? $"Token cleared in workspace '{DisplayName}'."
             : $"Token entered in workspace '{DisplayName}' ({value.Length} characters).");
      }
      _ = RefreshOrganizationsAsync();
   }



   /// <summary>
   /// Takes an account workspace's token from its source again (its own vault entry,
   /// or the default token from Settings) and uses it from here on. Called before the
   /// token is used and whenever the shell knows a saved token changed. Nothing
   /// happens for Quick Sync, whose token is whatever is in its box, or when the
   /// source still holds the same token.
   /// </summary>
   public void RefreshAccountToken()
   {
      if(_account is null || _tokenResolver is null)
      {
         return;
      }

      string token;
      try
      {
         token = _tokenResolver.TryResolve(AccountAsItIsNow()) ?? "";
      }
      catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
      {
         ///////////////////////////////////////////////////////////////////////////
         // The credential store did not answer. That says nothing about the      //
         // token: the one read earlier stays in use, and whatever needs it next  //
         // succeeds or fails on its own terms.                                    //
         ///////////////////////////////////////////////////////////////////////////
         _log.Error($"Workspace '{DisplayName}': its saved token could not be read ({ex.Message}); the one read earlier stays in use.", ex);
         return;
      }

      SetTokenFromItsSource(token);
   }



   /// <summary>
   /// The account as the store has it now: where its token comes from can have been
   /// changed (to the default token, or to a token of its own) since this workspace
   /// was opened. The record the workspace was opened with when the store no longer
   /// has the account, or when there is no store.
   /// </summary>
   /// <remarks>
   /// The place is always the one the workspace was opened for. Its listers were
   /// made for that place and stay; a record that names another place by now (the
   /// file was changed) must not bring the token of that other place to them.
   /// </remarks>
   private Account AccountAsItIsNow()
   {
      Account now = _accountsStore?.GetAll().FirstOrDefault(a => a.Id == _account!.Id) ?? _account!;
      return now with { ForgeName = _account!.ForgeName, Server = _account.Server };
   }



   /// <summary>
   /// Why this workspace has no token to work with, in words that name where to look;
   /// null when it has one. For callers that decide before starting anything (Sync
   /// all) and want to say why they did not.
   /// </summary>
   public string? MissingTokenDescription
       => Token.Trim().Length > 0
           ? null
           : _account is not null && _tokenResolver is not null
               ? $"Account '{_account.Name}' {_tokenResolver.MissingDescription(AccountAsItIsNow())}."
               : "There is no token: enter one in the token box.";



   /// <summary>
   /// What an account's page says when the account has no token to work with: where
   /// to save one, in the words of the window's own menus. Null when it has one, and
   /// for Quick Sync, whose token is typed into its box.
   /// </summary>
   public string? MissingTokenAdvice
       => _account is null || Token.Trim().Length > 0
           ? null
           : AccountAsItIsNow().UsesDefaultToken
               ? "No default token is saved. Save one in File > Settings, or give this account its own token with Edit account…"
               : "No token is saved for this account. Choose Edit account… and enter one.";



   /// <summary>
   /// Tells the workspace that the default token in Settings was saved, replaced, or
   /// removed. An account workspace takes its token from its source again. Quick
   /// Sync follows only when its box still holds what Settings put there
   /// (<paramref name="previous"/>, or nothing): a token somebody typed stays.
   /// </summary>
   /// <param name="previous">The default token before the change; null when there was none.</param>
   /// <param name="current">The default token now; null when there is none.</param>
   public void OnDefaultTokenChanged(string? previous, string? current)
   {
      if(_account is not null)
      {
         RefreshAccountToken();
      }
      else if(string.Equals(Token, previous ?? "", StringComparison.Ordinal))
      {
         SetTokenFromItsSource(current ?? "");
      }
   }



   private void SetTokenFromItsSource(string token)
   {
      if(string.Equals(token, Token, StringComparison.Ordinal))
      {
         return;
      }

      _readingTokenFromItsSource = true;
      try
      {
         Token = token;
      }
      finally
      {
         _readingTokenFromItsSource = false;
      }
   }



   /// <summary>
   /// The token to use right now: for an account, read from its source this moment,
   /// and a failure that names the source when it holds none.
   /// </summary>
   private string TokenToUseNow()
   {
      RefreshAccountToken();
      return MissingTokenDescription is { } missing
          ? throw new InvalidOperationException(missing)
          : Token.Trim();
   }



   /// <summary>Records that the user opened the target folder, from the toolbar or the results bar (#40).</summary>
   public void NoteFolderOpened() => _log.Info($"Opened folder '{EffectiveTargetRoot}'.");



   /// <summary>
   /// The user asked for the target folder and it does not exist: says so in the
   /// status line. Nothing has been synced into it yet; the first sync creates it.
   /// </summary>
   public void NoteFolderNotThereYet()
   {
      StatusText = $"The folder '{EffectiveTargetRoot}' does not exist yet. The first sync creates it.";
      _log.Info($"Folder '{EffectiveTargetRoot}' was asked for and does not exist yet.");
   }



   /// <summary>Asks every row to re-evaluate its theme-dependent presentation (see <see cref="RepoItemViewModel.RefreshPresentation"/>).</summary>
   public void RefreshPresentation()
   {
      foreach(RepoItemViewModel item in Repos)
      {
         item.RefreshPresentation();
      }
   }



   private bool _disposed;



   /// <summary>
   /// Stops the in-flight org lookup and path recovery, if any. Idempotent: the shell
   /// disposes workspaces on eviction and again on window close, and a second call
   /// used to throw ObjectDisposedException from the lifetime token source (#31).
   /// </summary>
   public void Dispose()
   {
      if(_disposed)
      {
         return;
      }
      _disposed = true;
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
      CancellationTokenSource cts = _orgLoadCts = new CancellationTokenSource();
      // Captured before any successor can dispose cts; used everywhere below.
      CancellationToken lookupToken = cts.Token;

      ///////////////////////////////////////////////////////////////////////////////
      // A lookup started because the saved token was read again is nobody's       //
      // action: it refreshes the list of organizations and says nothing. The load //
      // or sync that read the token owns the status line and the error bar, and a //
      // lookup that lands in the middle of it must not write over either.         //
      ///////////////////////////////////////////////////////////////////////////////
      bool quiet = _readingTokenFromItsSource;

      string token = Token.Trim();
      if(token.Length < 10)
      {
         // The same capture and restore as below: the editable box loses its text
         // when its list changes, and an account's organization must survive its
         // token going away (it is what the account syncs).
         string organizationBeforeClear = Organization;
         Organizations.Clear();
         Organization = organizationBeforeClear;
         OnPropertyChanged(nameof(Organization));
         // The newest invocation owns the flag: a canceled predecessor deliberately
         // leaves it alone, so an early return must clear any spinner it left behind.
         IsLoadingOrgs = false;
         return; // not plausibly a complete PAT yet
      }

      try
      {
         await Task.Delay(_orgLookupDebounce, lookupToken); // debounce keystrokes / rapid pastes
         IsLoadingOrgs = true;
         _log.Info("Organization lookup started.");
         IReadOnlyList<string> orgs = await _orgLister.ListOrganizationsAsync(token, lookupToken);
         lookupToken.ThrowIfCancellationRequested();
         _log.Info($"Organization lookup finished: {orgs.Count} organizations and accounts visible.");

         // An editable ComboBox resets its Text when its ItemsSource is mutated,
         // and the TwoWay binding would wipe a value that was already set — an
         // account workspace seeds Organization in the constructor, and this
         // refresh lands ~a second later. Capture, restore, and force a binding
         // resync so the seeded (or typed) organization survives the refresh.
         string organizationBeforeRefresh = Organization;
         Organizations.Clear();
         foreach(string org in orgs)
         {
            Organizations.Add(org);
         }
         Organization = organizationBeforeRefresh;
         OnPropertyChanged(nameof(Organization));
         if(!quiet)
         {
            LoadErrorOpen = false; // a lookup that works clears a stale lookup failure
            // The production lister always lists the token's own account first, so a
            // single entry means no organizations were visible.
            StatusText = orgs.Count <= 1
                ? "Only your personal account is visible — add read:org (classic) for organizations, or type an org name manually."
                : $"Found {orgs.Count} organizations and accounts.";
         }
      }
      catch(OperationCanceledException)
      {
         // superseded by a newer token edit
      }
      catch(Exception ex)
      {
         if(_orgLoadCts == cts)
         {
            _log.Error($"Organization lookup failed: {ex.Message}", ex);
            if(!quiet)
            {
               ShowLoadError("Could not list organizations", ex.Message);
            }
         }
      }
      finally
      {
         if(_orgLoadCts == cts)
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
         if(MaxConcurrency != clamped)
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
      if(_syncingSelection)
      {
         return; // being recomputed from an item change; do not push back down
      }

      _syncingSelection = true;
      try
      {
         foreach(RepoItemViewModel item in FilteredRepos)
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
      if(string.Equals(e.PropertyName, nameof(RepoItemViewModel.HasPathIssue), StringComparison.Ordinal))
      {
         // Parameterized command: the row's 'Resolve...' button re-queries CanExecute.
         ResolvePathsCommand.NotifyCanExecuteChanged();
         return;
      }
      if(!string.Equals(e.PropertyName, nameof(RepoItemViewModel.IsSelected), StringComparison.Ordinal))
      {
         return;
      }
      if(!_syncingSelection)
      {
         // A header push (or any other batch) recomputes once after its loop
         // instead of per row — including the command re-query, whose CanSync
         // scans every row (#30).
         RecomputeSelectedCount();
         UpdateAllSelectedFromItems();
         SyncCommand.NotifyCanExecuteChanged();
      }
   }



   private void RecomputeSelectedCount() => SelectedCount = Repos.Count(r => r.IsSelected);



   /// <summary>Header state follows the VISIBLE rows; see <see cref="AllSelected"/>.</summary>
   private void UpdateAllSelectedFromItems()
   {
      bool all = FilteredRepos.All(r => r.IsSelected);
      if(AllSelected != all)
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
      LoadErrorOpen = false;
      try
      {
         string organization = Organization.Trim();
         string token = TokenToUseNow();
         StatusText = "Loading repositories...";
         _log.Info($"Loading repositories for organization '{organization}'.");

         var descriptors = (await _lister
             .ListOrganizationRepositoriesAsync(organization, token))
             // Names key the row dictionary; a duplicate (possible from pagination
             // shifts) must not produce two rows fighting over one folder.
             .DistinctBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
             .ToList();

         foreach(RepoItemViewModel existing in Repos)
         {
            existing.PropertyChanged -= OnItemPropertyChanged;
         }
         Repos.Clear();
         _itemsByName.Clear();

         foreach(RepoDescriptor descriptor in descriptors)
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
         RebuildReposIndex();
         LoadedOrganization = organization;
         HasLoadedRepos = true;
         StatusText = Repos.Count == 1 ? "1 repository loaded." : $"{Repos.Count} repositories loaded.";
         _log.Info($"Loaded {Repos.Count} repositories for organization '{organization}'.");
      }
      catch(Exception ex)
      {
         // The message is good (the listers translate 401/404/rate limits); the
         // surface must be too — an InfoBar with Retry, not caption text at the
         // bottom of the window (#30).
         ShowLoadError("Could not load repositories", ex.Message);
         _log.Error($"Loading repositories failed: {ex.Message}", ex);
      }
      finally
      {
         IsLoadingRepos = false;
         NotifyTargetPathChanged();
         RecomputeSelectedCount();
         RebuildFilteredRepos(); // also re-derives AllSelected from the visible rows
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
       && !IsTableStale
       && !string.IsNullOrWhiteSpace(TargetFolder)
       && Repos.Any(r => r.IsSelected);



   /// <summary>Phase two: clones or updates the selected repositories.</summary>
   [RelayCommand(CanExecute = nameof(CanSync), IncludeCancelCommand = true)]
   private async Task SyncAsync(CancellationToken cancellationToken)
   {
      IsRunning = true;
      ResetRunResult();
      try
      {
         // First, before any row is touched: a sync that cannot start for want of a
         // token leaves the table (failures, their paths, the retry) as it was.
         string token = TokenToUseNow();

         List<RepoItemViewModel> selected = _runOnly ?? [.. Repos.Where(r => r.IsSelected)];
         foreach(RepoItemViewModel item in selected)
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
         RebuildFilteredRepos(); // every selected row just went back to Queued

         string targetRoot = EffectiveTargetRoot;
         StatusText = selected.Count == 1 ? "Syncing 1 repository..." : $"Syncing {selected.Count} repositories...";
         _log.Info($"Sync started: {selected.Count} repositories into '{targetRoot}'.");

         // Constructed on the UI thread: the delivery captures the WinUI
         // SynchronizationContext, so HandleProgress always runs on the UI thread,
         // a few times a second with each repository's latest state rather than
         // once per report (#167). Failure logging is layered in front of it so the
         // per-entry file I/O happens on the engine's worker thread, never on the
         // dispatcher (#30).
         IProgress<RepoProgress> delivery = _progressFactory(HandleProgress);
         var progress = new FailureLoggingProgress(delivery, _log);

         var request = new SyncRequest(
             Organization.Trim(), token, targetRoot, MaxConcurrency);
         IReadOnlyList<RepoDescriptor> descriptors = selected.Select(r => r.Descriptor).ToList();

         Func<SyncRequest, IReadOnlyList<RepoDescriptor>, IProgress<RepoProgress>, CancellationToken, Task<SyncSummary>> run =
             SyncRunner ?? ((req, descs, prog, ct) => new OrgSyncEngine(_lister, _git).SyncAsync(req, descs, prog, ct));
         SyncSummary summary;
         try
         {
            summary = await run(request, descriptors, progress, cancellationToken);
         }
         finally
         {
            // Whatever still waits to be delivered reaches the rows now, before the
            // outcome is stated.
            (delivery as IDisposable)?.Dispose();
         }

         string summaryText = (summary.WasCanceled ? "Canceled" : "Finished")
             + $": {summary.Cloned} cloned, {summary.Updated} updated, "
             + $"{summary.Failed} failed, {summary.Canceled} canceled of {summary.Total}.";
         StatusText = summaryText;
         _log.Info(summaryText);

         ResultKind = summary.WasCanceled ? RunResultKind.Canceled
             : summary.Failed > 0 ? RunResultKind.PartialFailure
             : RunResultKind.Success;
         ResultMessage = summaryText;

         if(_account is not null && _accountsStore is not null)
         {
            try
            {
               _accountsStore.RecordSyncResult(_account.Id, DateTimeOffset.UtcNow, summaryText);
            }
            catch(Exception ex)
            {
               // Bookkeeping only: failing to stamp the account must not turn a
               // completed sync into an error.
               _log.Error(
                   $"Failed to record the sync result for account '{_account.Name}': {ex.Message}", ex);
            }
         }
      }
      catch(OperationCanceledException)
      {
         StatusText = "Canceled";
         ResultKind = RunResultKind.Canceled;
         ResultMessage = "Canceled";
         _log.Info("Sync canceled.");
      }
      catch(Exception ex)
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
         RebuildFilteredRepos();
         ResultOpen = true;
      }
   }



   /// <summary>How progress reaches the window when nothing else was asked for: in batches (#167).</summary>
   internal static IProgress<RepoProgress> DefaultProgress(Action<RepoProgress> handler)
       => new BatchedRepoProgress(handler);



   // ---------------------------------------------------------------- retry

   private bool CanRetryFailed() =>
       !IsRunning
       && !IsLoadingRepos
       && !IsResolvingPaths
       && Repos.Any(r => r.Status == SyncStatus.Failed);



   /// <summary>
   /// Runs the same sync path again for exactly the failed repositories. The
   /// selection is not touched: the retry is one run, not a change of what the next
   /// sync covers. (It used to select the failed rows; a later Sync, or Sync all,
   /// then synced only those and reported the account as synced, #182.)
   /// </summary>
   [RelayCommand(CanExecute = nameof(CanRetryFailed))]
   private async Task RetryFailedAsync()
   {
      _runOnly = [.. Repos.Where(r => r.Status == SyncStatus.Failed)];
      _log.Info($"Retrying {_runOnly.Count} failed repositories.");
      try
      {
         // Executing SyncCommand itself keeps SyncCancelCommand working for retry runs.
         await SyncCommand.ExecuteAsync(null);
      }
      finally
      {
         _runOnly = null;
      }
   }



   /// <summary>
   /// The repositories the run that is starting takes instead of the selected ones;
   /// null for an ordinary sync. Set by <see cref="RetryFailedAsync"/> for its one run.
   /// </summary>
   private List<RepoItemViewModel>? _runOnly;



   // ---------------------------------------------------------------- path recovery

   /// <summary>
   /// Raised when the user asks to resolve a row's Windows-invalid paths. The view
   /// subscribes and shows recovery UI (the VM stays UI-free), returning the user's
   /// decision, or null when they cancel. With no subscriber the command does nothing.
   /// <see cref="IsWslCloneAvailable"/> is settled before the interaction runs, so the
   /// view can decide whether to offer "Clone in WSL instead".
   /// </summary>
   public Func<RepoItemViewModel, Task<PathRecoveryDecision?>>? RecoveryInteraction { get; set; }

   /// <summary>
   /// True when a clone into WSL can be offered as a recovery (#8): wsl.exe exists, the
   /// default distribution starts, and git runs inside it. Probed once, lazily, the
   /// first time path recovery is requested; false until then.
   /// </summary>
   [ObservableProperty]
   public partial bool IsWslCloneAvailable { get; private set; }



   /// <summary>Why <see cref="IsWslCloneAvailable"/> is false (or the git version when true), for the log and the dialog.</summary>
   public string WslAvailabilityDetail => _wslAvailability?.Detail ?? "";



   private async Task EnsureWslProbedAsync(CancellationToken cancellationToken)
   {
      if(_wslAvailability is not null)
      {
         return;
      }
      WslAvailability availability = await _wsl.ProbeAsync(cancellationToken);
      _wslAvailability = availability;
      IsWslCloneAvailable = availability.Available;
      _log.Info(availability.Available
          ? $"WSL clone available ({availability.Detail})."
          : $"WSL clone not available: {availability.Detail}");
   }



   private bool CanResolvePaths(RepoItemViewModel item)
       => item is not null && item.HasPathIssue && !IsRunning && !IsResolvingPaths;



   /// <summary>
   /// Asks the view (via <see cref="RecoveryInteraction"/>) how to rename or skip the
   /// row's invalid paths, then applies that recovery and checks the repository out.
   /// </summary>
   [RelayCommand(CanExecute = nameof(CanResolvePaths))]
   private async Task ResolvePathsAsync(RepoItemViewModel item)
   {
      if(item is null || RecoveryInteraction is not { } interaction)
      {
         return;
      }

      _log.Info($"{item.Name}: path recovery requested ({item.InvalidPaths?.Count ?? 0} invalid paths).");
      await EnsureWslProbedAsync(_lifetimeCts.Token);
      PathRecoveryDecision? decision = await interaction(item);
      if(decision is null)
      {
         _log.Info($"{item.Name}: path recovery canceled; the repository stays failed.");
         return; // user canceled; the row keeps its Failed state and payload
      }

      IsResolvingPaths = true;
      try
      {
         if(decision is PathRecoveryDecision.CloneInWsl)
         {
            await CloneInWslAsync(item);
         }
         else
         {
            await ApplyRecoveryAsync(item, ((PathRecoveryDecision.Apply)decision).Recovery);
         }
      }
      finally
      {
         IsResolvingPaths = false;
      }
      RecomputeCompletedCount();
      PlaceInTable(item);
      RetryFailedCommand.NotifyCanExecuteChanged();
   }



   private async Task ApplyRecoveryAsync(RepoItemViewModel item, PathRecovery recovery)
   {
      string path = Path.Combine(EffectiveTargetRoot, item.Name);
      try
      {
         _log.Info($"{item.Name}: applying path recovery "
             + $"({recovery.SegmentRenames.Count} renamed, {recovery.SkippedPaths.Count} skipped).");
         // Pulling shows the row's indeterminate bar while the checkout runs.
         SetStatus(item, SyncStatus.Pulling);
         await _git.ApplyRecoveryAsync(path, recovery, _lifetimeCts.Token);

         SetStatus(item, SyncStatus.Done);
         item.Error = null;
         item.InvalidPaths = null;
         _log.Info($"{item.Name}: path recovery applied; repository checked out.");
      }
      catch(Exception ex)
      {
         SetStatus(item, SyncStatus.Failed);
         item.Error = ex.Message;
         // A still-invalid or colliding mapping keeps the row resolvable with the
         // fresh path list; any other failure clears the payload (renaming again
         // would not help).
         item.InvalidPaths = (ex as InvalidRepositoryPathsException)?.Paths;
         _log.Error($"{item.Name}: path recovery failed: {ex.Message}", ex);
      }
   }



   /// <summary>
   /// The "Clone in WSL instead" recovery (#8): the repository is cloned by the default
   /// WSL distribution's git into ~/gclo/&lt;org&gt;/&lt;repo&gt;, where its paths are
   /// legal. The fetched-but-unchecked-out Windows copy is left as it is (a later
   /// Windows sync reports the same invalid paths again, by design: nothing on the
   /// Windows side changed). On failure the row keeps its payload so the user can pick
   /// the other recovery.
   /// </summary>
   private async Task CloneInWslAsync(RepoItemViewModel item)
   {
      try
      {
         _log.Info($"{item.Name}: cloning in WSL instead.");
         SetStatus(item, SyncStatus.Pulling); // indeterminate bar; git in WSL reports no percentage here
         // The organization the table was loaded for, not whatever the box says now:
         // the row belongs to that organization, and its folder under ~/gclo should too.
         WslCloneResult result = await _wsl.CloneAsync(
             item.Descriptor.CloneUrl, LoadedOrganization ?? Organization.Trim(), item.Name, TokenToUseNow(), _lifetimeCts.Token);

         SetStatus(item, SyncStatus.Done);
         item.Error = null;
         item.InvalidPaths = null;
         item.Note = $"Cloned in WSL ({result.Distribution}): {result.WindowsPath}";
         _log.Info($"{item.Name}: cloned in WSL ({result.Distribution}) at {result.LinuxPath}.");
      }
      catch(Exception ex)
      {
         SetStatus(item, SyncStatus.Failed);
         item.Error = ex.Message;
         _log.Error($"{item.Name}: clone in WSL failed: {ex.Message}", ex);
      }
   }



   // ---------------------------------------------------------------- sorting

   /// <summary>
   /// Sorts the table by a column ("Name", "Status", "Branch", or "Archived"); a second
   /// click on the same column flips the direction, and a third puts the table back
   /// in its default order (see <see cref="SortColumn"/>). The re-order is stable.
   /// </summary>
   [RelayCommand]
   private void Sort(string column)
   {
      if(column is not ("Name" or "Status" or "Branch" or "Archived"))
      {
         return;
      }

      if(string.Equals(SortColumn, column, StringComparison.Ordinal))
      {
         if(SortDescending)
         {
            SortColumn = null;
            SortDescending = false;
            RebuildFilteredRepos();
            return;
         }
         SortDescending = true;
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

      // Nothing binds Repos (the table binds FilteredRepos), so a plain reorder is
      // enough: O(n) adds instead of the O(n^2) IndexOf/Move choreography that
      // used to run here, and the one rebuild below refreshes the bound list (#30).
      Repos.Clear();
      foreach(RepoItemViewModel item in sorted)
      {
         Repos.Add(item);
      }
      RebuildReposIndex();

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
   public event EventHandler<AnnouncementEventArgs>? AnnouncementRequested;



   /// <summary>Applies one engine progress report to the table. Runs on the UI thread.</summary>
   private void HandleProgress(RepoProgress report)
   {
      if(!_itemsByName.TryGetValue(report.RepoName, out RepoItemViewModel? item))
      {
         return; // rows are created at load time; ignore anything unknown
      }

      SyncStatus statusBefore = item.Status;
      if(report.Status != SyncStatus.Queued)
      {
         item.Status = report.Status;
         item.Error = report.Error;
         item.Percent = report.Percent;
         item.InvalidPaths = report.InvalidPaths;
      }

      if(report.Status == SyncStatus.Failed)
      {
         // Logged already by FailureLoggingProgress on the worker thread. Failures
         // never reach StatusText (which has a live region), so assistive
         // technology hears them only through this explicit channel.
         AnnouncementRequested?.Invoke(this, new AnnouncementEventArgs($"{report.RepoName} failed. {report.Error}"));
      }

      if(report.Status is SyncStatus.Done or SyncStatus.Failed or SyncStatus.Canceled)
      {
         RecomputeCompletedCount();
      }

      if(statusBefore != item.Status)
      {
         // Status-dependent filters follow every transition (a row entering Cloning
         // leaves Pending and joins Active immediately), and a row in flight sits
         // at the top of the table (#168). Only this one row can have changed its
         // membership or its place, so it alone is inserted, removed, or moved — no
         // O(n) rebuild, no Reset notification, no scroll jump (#30).
         item.LastActivity = ++_activityClock;
         PlaceInTable(item);
      }
   }



   /// <summary>
   /// Logs failed reports on the thread that produced them (the engine's worker), then
   /// forwards every report to the UI-marshaled progress. FileActivityLog opens,
   /// appends, and closes the file per entry; a mass failure (revoked PAT, dropped
   /// network) used to put a thousand of those on the UI thread (#30).
   /// </summary>
   private sealed class FailureLoggingProgress(IProgress<RepoProgress> inner, IActivityLog log)
       : IProgress<RepoProgress>
   {
      public void Report(RepoProgress value)
      {
         if(value.Status == SyncStatus.Failed)
         {
            log.Error($"{value.RepoName} failed: {value.Error}");
         }
         inner.Report(value);
      }
   }



   /// <summary>
   /// Recounts terminal rows within the captured run set (#22) — before the first run
   /// the set is empty and the count stays 0. Also refreshes the failed-rows flag.
   /// </summary>
   private void RecomputeCompletedCount()
   {
      int completed = 0;
      foreach(RepoItemViewModel repo in _runSet)
      {
         if(repo.Status is SyncStatus.Done or SyncStatus.Failed or SyncStatus.Canceled)
         {
            completed++;
         }
      }
      CompletedCount = completed;
      RecomputeHasFailedRepos();
   }



   private void RecomputeHasFailedRepos()
       => HasFailedRepos = Repos.Any(r => r.Status == SyncStatus.Failed);



   // ---------------------------------------------------------------- filtering

   /// <summary>Refreshes the row -> position map after <see cref="Repos"/> was reloaded or reordered.</summary>
   private void RebuildReposIndex()
   {
      _reposIndex.Clear();
      for(int i = 0; i < Repos.Count; i++)
      {
         _reposIndex[Repos[i]] = i;
      }
   }



   /// <summary>
   /// Rebuilds <see cref="FilteredRepos"/> from <see cref="Repos"/>, preserving the
   /// table's current sort order, then re-derives the header checkbox from the
   /// visible rows. Used when the filter, the table contents, or the sort order
   /// change; single-row status transitions go through
   /// <see cref="PlaceInTable"/> instead. A no-op when the visible set
   /// is already correct, so list controls keep their scroll position.
   /// </summary>
   private void RebuildFilteredRepos()
   {
      var desired = Repos.Where(MatchesFilter).ToList();
      desired.Sort(CompareForTable);
      if(!desired.SequenceEqual(FilteredRepos))
      {
         FilteredRepos.Clear();
         _filteredSet.Clear();
         foreach(RepoItemViewModel repo in desired)
         {
            FilteredRepos.Add(repo);
            _filteredSet.Add(repo);
         }
      }
      UpdateAllSelectedFromItems();
   }



   /// <summary>True while a git operation is running for the row.</summary>
   private static bool IsInFlight(RepoItemViewModel repo)
       => repo.Status is SyncStatus.Cloning or SyncStatus.Pulling;



   /// <summary>
   /// Which group a row belongs to in the default order: the ones in flight (0), the
   /// failed ones (1), while a sync runs the finished ones (2), and the rest (3).
   /// </summary>
   private int GroupOf(RepoItemViewModel repo)
   {
      if(IsInFlight(repo))
      {
         return 0;
      }
      if(repo.Status == SyncStatus.Failed)
      {
         return 1;
      }
      return IsRunning && repo.Status is SyncStatus.Done or SyncStatus.Canceled ? 2 : 3;
   }



   /// <summary>
   /// The table's order, as a comparison (negative: <paramref name="a"/> comes first).
   /// With a column chosen by the user it is that column's order, which
   /// <see cref="Repos"/> holds. Otherwise it is the default order (#168): what is
   /// happening now is on top and the list moves as the sync goes. While a sync runs:
   /// the rows in flight (the one that started first on top), the failed ones and
   /// then the finished ones (the latest first in each), then the ones still waiting,
   /// by name. With no sync running: the failed ones, then everything else, by name.
   /// Never 0 for two different rows, so every row has exactly one place.
   /// </summary>
   private int CompareForTable(RepoItemViewModel a, RepoItemViewModel b)
   {
      if(SortColumn is null)
      {
         int groupA = GroupOf(a);
         int groupB = GroupOf(b);
         if(groupA != groupB)
         {
            return groupA.CompareTo(groupB);
         }

         int byActivity = groupA switch
         {
            0 => a.LastActivity.CompareTo(b.LastActivity),
            1 or 2 when IsRunning => b.LastActivity.CompareTo(a.LastActivity),
            _ => 0,
         };
         if(byActivity != 0)
         {
            return byActivity;
         }

         // A table holds one row per name, whatever its case (the load sees to it).
         return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
      }
      return _reposIndex[a].CompareTo(_reposIndex[b]);
   }



   /// <summary>Sets a row's status outside a sync run (a recovery) and puts the row where it now belongs.</summary>
   private void SetStatus(RepoItemViewModel item, SyncStatus status)
   {
      item.Status = status;
      item.LastActivity = ++_activityClock;
      PlaceInTable(item);
   }



   /// <summary>
   /// Brings one row's presence and place in <see cref="FilteredRepos"/> in line with
   /// its current state: inserted where it belongs when it now matches the filter,
   /// removed when it no longer does, moved when its place changed (it started,
   /// finished, or failed), and left alone otherwise. One row, not the former O(n)
   /// rebuild per progress report.
   /// </summary>
   private void PlaceInTable(RepoItemViewModel item)
   {
      if(!_reposIndex.ContainsKey(item))
      {
         // A row from before a reload (its path recovery was still open when the
         // table was loaded again): it has no place in this table.
         return;
      }

      bool visible = MatchesFilter(item);
      int current = _filteredSet.Contains(item) ? FilteredRepos.IndexOf(item) : -1;
      if(!visible && current < 0)
      {
         return;
      }

      int slot = visible ? FindFilteredSlot(item, current) : -1;
      if(slot == current)
      {
         return;
      }

      if(current >= 0)
      {
         FilteredRepos.RemoveAt(current);
         _filteredSet.Remove(item);
      }
      if(visible)
      {
         FilteredRepos.Insert(slot, item);
         _filteredSet.Add(item);
      }
      UpdateAllSelectedFromItems();
   }



   /// <summary>
   /// Index at which <paramref name="item"/> belongs in <see cref="FilteredRepos"/>,
   /// counted without the item itself (<paramref name="current"/> is where it sits
   /// now, or -1 when it is not in the list), by <see cref="CompareForTable"/>. The
   /// other rows are in that order already, so a binary search finds it.
   /// </summary>
   private int FindFilteredSlot(RepoItemViewModel item, int current)
   {
      int low = 0;
      int high = current < 0 ? FilteredRepos.Count : FilteredRepos.Count - 1;
      while(low < high)
      {
         int mid = (low + high) / 2;
         RepoItemViewModel other = FilteredRepos[current >= 0 && mid >= current ? mid + 1 : mid];
         if(CompareForTable(other, item) < 0)
         {
            low = mid + 1;
         }
         else
         {
            high = mid;
         }
      }
      return low;
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
