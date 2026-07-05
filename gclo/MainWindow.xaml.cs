using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using gclo.Engine;
using gclo.Services;
using gclo.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace gclo
{
    /// <summary>
    /// Shell window: the menu bar plus a navigation pane with the pinned Quick Sync
    /// workspace and one workspace per saved account. Each workspace (a
    /// <see cref="WorkspacePage"/> and its <see cref="WorkspaceViewModel"/>) is created
    /// on first visit and cached, so switching away never interrupts a running sync;
    /// pane badges surface running/failed state for the workspaces not on screen.
    /// The pane's footer carries two commands: 'Sync all' (runs every account
    /// sequentially via <see cref="SyncAllCoordinator"/>) and 'Add account' (the
    /// <see cref="AccountWizardDialog"/>); account items get an Edit/Delete context menu.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private const string RepoUrl = "https://github.com/KofTwentyTwo/gclo";

        // Smallest logical (DPI-independent) size at which the workspace toolbar,
        // connect card, and repo table remain usable.
        private const int MinWindowWidth = 700;
        private const int MinWindowHeight = 520;

        private readonly AppSettings _settings;
        private readonly UpdateService _updateService = new();

        // One log, vault, and store shared by every workspace (and the log viewer),
        // so all workspaces write to the same file and read the same accounts.
        private readonly IActivityLog _log;
        private readonly ITokenVault _tokenVault;
        private readonly AccountsStore _accountsStore;

        /// <summary>Workspaces created so far, keyed by account id (Guid.Empty = Quick Sync).</summary>
        private readonly Dictionary<Guid, (WorkspaceViewModel ViewModel, WorkspacePage Page)> _workspaces = new();

        /// <summary>Badge subscriptions per workspace, unhooked before the view models are disposed.</summary>
        private readonly Dictionary<Guid, PropertyChangedEventHandler> _badgeHandlers = new();

        /// <summary>Navigation items by workspace id, for badge and tooltip refreshes.</summary>
        private readonly Dictionary<Guid, NavigationViewItem> _navItems = new();

        /// <summary>The workspace the pane currently rests on; where the footer commands revert to.</summary>
        private Guid _currentWorkspaceId = Guid.Empty;

        /// <summary>Suppresses re-entrant SelectionChanged while the code puts a selection back.</summary>
        private bool _revertingSelection;

        /// <summary>
        /// Cancels the in-flight 'Sync all' queue; null while none runs. The token source
        /// itself stays local to <see cref="ToggleSyncAllAsync"/> so its lifetime is
        /// contained there — only the ability to cancel escapes.
        /// </summary>
        private Action? _cancelSyncAll;

        public MainWindow()
        {
            _log = new FileActivityLog(System.IO.Path.Combine(GcloPaths.DataRoot, "logs"));
            App.CrashLog = _log; // the unhandled-exception net now reaches the activity log
            _tokenVault = new CredentialManagerVault();
            _accountsStore = new AccountsStore(_tokenVault, log: _log);

            InitializeComponent();

            Title = "gclo — Git Clone Large Organizations";

            // Quick Sync is declared in XAML; its Guid tag and the account items are
            // runtime data, so they are filled in here.
            QuickSyncNavItem.Tag = Guid.Empty;
            _navItems[Guid.Empty] = QuickSyncNavItem;
            foreach (Account account in _accountsStore.GetAll())
            {
                WorkspaceNav.MenuItems.Add(CreateAccountNavItem(account));
            }
            UpdateSyncAllEnabled();

            _settings = AppSettings.Load();

            // Create and show Quick Sync before selecting it, so NavigationView.Content
            // is never empty regardless of when SelectionChanged fires.
            ShowWorkspace(Guid.Empty);
            ApplySettings();
            WorkspaceNav.SelectedItem = QuickSyncNavItem;

            // AppWindow.Resize takes physical pixels; scale by the monitor DPI so the
            // window is the same visual size at 150%/200% display scaling.
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            double scale = GetDpiForWindow(hwnd) / 96.0;
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1000 * scale), (int)(750 * scale)));

            // Keep the window from shrinking below a usable workspace layout.
            // PreferredMinimum* takes physical pixels like Resize (WinAppSDK 1.7+,
            // present in the installed SDK). If a future SDK removes these
            // properties, clamp instead from an AppWindow.Changed handler.
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = (int)(MinWindowWidth * scale);
                presenter.PreferredMinimumHeight = (int)(MinWindowHeight * scale);
            }

            Closed += (_, _) => DisposeWorkspaces();

            // The splash overlay honors Settings → Advanced: skipped entirely when
            // disabled, otherwise dismissed after the configured display time.
            if (_settings.ShowSplashScreen)
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    await Task.Delay(_settings.SplashMilliseconds);
                    await StartupSplash.DismissAsync();
                });
            }
            else
            {
                StartupSplash.Visibility = Visibility.Collapsed;
            }
        }

        private void ApplySettings()
        {
            if (Content is FrameworkElement root)
            {
                root.RequestedTheme = StatusFormat.ToElementTheme(_settings.Theme);
            }

            // Settings defaults only seed the ad-hoc Quick Sync workspace; account
            // workspaces carry their own configuration.
            if (_workspaces.TryGetValue(Guid.Empty, out var quickSync))
            {
                WorkspaceViewModel viewModel = quickSync.ViewModel;
                if (string.IsNullOrWhiteSpace(viewModel.TargetFolder)
                    && !string.IsNullOrWhiteSpace(_settings.DefaultTargetFolder))
                {
                    viewModel.TargetFolder = _settings.DefaultTargetFolder;
                }
                viewModel.MaxConcurrency = _settings.DefaultMaxConcurrency;
            }
        }

        // ---------------------------------------------------------------- workspaces

        private async void WorkspaceNav_SelectionChanged(
            NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (_revertingSelection || args.SelectedItem is not NavigationViewItem item)
            {
                return;
            }

            if (item.Tag is "add")
            {
                // The footer entries are commands, not destinations: put the selection
                // back on the current workspace, then run the command.
                RevertSelectionToCurrent();
                await ShowAccountWizardAsync(existing: null);
                return;
            }

            if (item.Tag is "syncall")
            {
                RevertSelectionToCurrent();
                await ToggleSyncAllAsync();
                return;
            }

            if (item.Tag is Guid id)
            {
                ShowWorkspace(id);
                if (_workspaces.ContainsKey(id))
                {
                    _currentWorkspaceId = id;
                }
            }
        }

        /// <summary>
        /// Puts the selection back on the current workspace after a footer command
        /// (a command, not a destination) was activated.
        /// </summary>
        private void RevertSelectionToCurrent()
        {
            _revertingSelection = true;
            try
            {
                WorkspaceNav.SelectedItem =
                    _navItems.TryGetValue(_currentWorkspaceId, out NavigationViewItem? current)
                        ? current
                        : QuickSyncNavItem;
            }
            finally
            {
                _revertingSelection = false;
            }
        }

        /// <summary>
        /// Puts the workspace for <paramref name="id"/> on screen, creating and caching
        /// it on first visit. Cached view models keep running syncs alive across switches.
        /// </summary>
        private void ShowWorkspace(Guid id)
        {
            if (EnsureWorkspace(id) is { } workspace)
            {
                WorkspaceNav.Content = workspace.Page;
            }
        }

        /// <summary>
        /// Returns the cached workspace for <paramref name="id"/>, creating and caching
        /// it (badge subscription included) on first use WITHOUT putting it on screen —
        /// 'Sync all' warms unvisited workspaces this way. Null when the account no
        /// longer exists in the store.
        /// </summary>
        private (WorkspaceViewModel ViewModel, WorkspacePage Page)? EnsureWorkspace(Guid id)
        {
            if (_workspaces.TryGetValue(id, out var workspace))
            {
                return workspace;
            }

            WorkspaceViewModel? viewModel = CreateWorkspaceViewModel(id);
            if (viewModel is null)
            {
                return null; // stale item: the account no longer exists in the store
            }

            var page = new WorkspacePage(
                viewModel, () => WinRT.Interop.WindowNative.GetWindowHandle(this));
            workspace = (viewModel, page);
            _workspaces[id] = workspace;

            PropertyChangedEventHandler handler = (_, e) =>
            {
                if (e.PropertyName is nameof(WorkspaceViewModel.IsRunning)
                    or nameof(WorkspaceViewModel.HasFailedRepos))
                {
                    OnWorkspaceStateChanged(id);
                }
            };
            viewModel.PropertyChanged += handler;
            _badgeHandlers[id] = handler;
            return workspace;
        }

        private WorkspaceViewModel? CreateWorkspaceViewModel(Guid id)
        {
            if (id == Guid.Empty)
            {
                // Quick Sync: no account behind it. A saved default token (Settings)
                // pre-fills the connect card; setting Token also starts the org lookup.
                var quickSync = new WorkspaceViewModel(log: _log);
                if (_tokenVault.TryRetrieve(AppSettings.DefaultTokenVaultId) is { Length: > 0 } defaultToken)
                {
                    quickSync.Token = defaultToken;
                }
                return quickSync;
            }

            Account? account = _accountsStore.GetAll().FirstOrDefault(a => a.Id == id);
            if (account is null)
            {
                _log.Error($"No account with id {id:N}; cannot open its workspace.");
                return null;
            }
            return new WorkspaceViewModel(
                account: account,
                tokenVault: _tokenVault,
                accountsStore: _accountsStore,
                log: _log);
        }

        /// <summary>
        /// Refreshes a workspace's pane badge (attention dot while running, critical dot
        /// when it has failures) and, after a run, its tooltip from the stored summary.
        /// </summary>
        private void OnWorkspaceStateChanged(Guid id)
        {
            if (!_navItems.TryGetValue(id, out NavigationViewItem? item)
                || !_workspaces.TryGetValue(id, out var workspace))
            {
                return;
            }

            WorkspaceViewModel viewModel = workspace.ViewModel;
            item.InfoBadge = viewModel.IsRunning
                ? CreateDotBadge("AttentionDotInfoBadgeStyle", "SystemFillColorAttentionBrush")
                : viewModel.HasFailedRepos
                    ? CreateDotBadge("CriticalDotInfoBadgeStyle", "SystemFillColorCriticalBrush")
                    : null;

            // A finished run has stamped its outcome onto the account by now.
            if (!viewModel.IsRunning && id != Guid.Empty)
            {
                Account? account = _accountsStore.GetAll().FirstOrDefault(a => a.Id == id);
                if (account is not null)
                {
                    ToolTipService.SetToolTip(item, account.LastSyncSummary ?? "Never synced");
                }
            }
        }

        /// <summary>
        /// A value-less (dot) InfoBadge, styled by the WinUI dot-badge resource when
        /// present, otherwise tinted directly from the matching theme fill brush.
        /// </summary>
        private static InfoBadge CreateDotBadge(string styleKey, string fallbackBrushKey)
        {
            var badge = new InfoBadge();
            if (Application.Current.Resources.TryGetValue(styleKey, out object? styleValue)
                && styleValue is Style style)
            {
                badge.Style = style;
            }
            else if (Application.Current.Resources.TryGetValue(fallbackBrushKey, out object? brushValue)
                && brushValue is Brush brush)
            {
                badge.Background = brush;
            }
            return badge;
        }

        // ---------------------------------------------------------------- accounts

        /// <summary>
        /// Builds the pane item for an account — name, icon, last-sync tooltip, and the
        /// Edit/Delete context menu — and registers it for badge and tooltip refreshes.
        /// </summary>
        private NavigationViewItem CreateAccountNavItem(Account account)
        {
            var item = new NavigationViewItem
            {
                Content = account.Name,
                Tag = account.Id,
                Icon = new FontIcon { Glyph = "" },
                ContextFlyout = CreateAccountContextFlyout(account.Id),
            };
            ToolTipService.SetToolTip(item, account.LastSyncSummary ?? "Never synced");
            _navItems[account.Id] = item;
            return item;
        }

        /// <summary>The Edit/Delete context menu for an account's pane item.</summary>
        private MenuFlyout CreateAccountContextFlyout(Guid id)
        {
            var edit = new MenuFlyoutItem
            {
                Text = "Edit…",
                Icon = new FontIcon { Glyph = "" },
            };
            edit.Click += async (_, _) => await ShowEditAccountWizardAsync(id);

            var delete = new MenuFlyoutItem
            {
                Text = "Delete…",
                Icon = new FontIcon { Glyph = "" },
            };
            delete.Click += async (_, _) => await ConfirmDeleteAccountAsync(id);

            var flyout = new MenuFlyout();
            flyout.Items.Add(edit);
            flyout.Items.Add(delete);
            return flyout;
        }

        /// <summary>
        /// Inserts an account item into the pane's account section, keeping the section
        /// sorted by name (case-insensitive) like <see cref="AccountsStore.GetAll"/>.
        /// Only Guid-tagged items other than Quick Sync are accounts; everything before
        /// them (Quick Sync, the separator) is skipped by the tag check.
        /// </summary>
        private void InsertAccountNavItemSorted(NavigationViewItem item)
        {
            string name = item.Content as string ?? "";
            int insertAt = WorkspaceNav.MenuItems.Count;
            for (int i = 0; i < WorkspaceNav.MenuItems.Count; i++)
            {
                if (WorkspaceNav.MenuItems[i] is NavigationViewItem { Tag: Guid id, Content: string existing }
                    && id != Guid.Empty
                    && StringComparer.OrdinalIgnoreCase.Compare(name, existing) < 0)
                {
                    insertAt = i;
                    break;
                }
            }
            WorkspaceNav.MenuItems.Insert(insertAt, item);
        }

        /// <summary>
        /// Runs the account wizard: adding when <paramref name="existing"/> is null,
        /// editing (seeded from the account and its vault token) otherwise. On save,
        /// the pane is updated accordingly; on cancel nothing changes.
        /// </summary>
        private async Task ShowAccountWizardAsync(Account? existing)
        {
            string? existingToken = existing is null ? null : _tokenVault.TryRetrieve(existing.Id);
            var viewModel = new AccountWizardViewModel(
                _accountsStore, new GitHubOrganizationLister(), _settings, existing, existingToken);
            var dialog = new AccountWizardDialog(
                viewModel, () => WinRT.Interop.WindowNative.GetWindowHandle(this))
            {
                XamlRoot = Content.XamlRoot,
            };
            await DialogGuard.ShowAsync(dialog);
            if (!dialog.Saved)
            {
                return;
            }

            if (existing is null)
            {
                OnAccountAdded(viewModel);
            }
            else
            {
                OnAccountEdited(existing.Id);
            }
        }

        private async Task ShowEditAccountWizardAsync(Guid id)
        {
            Account? account = _accountsStore.GetAll().FirstOrDefault(a => a.Id == id);
            if (account is null)
            {
                return; // deleted meanwhile; the stale item is on its way out
            }
            await ShowAccountWizardAsync(account);
        }

        /// <summary>
        /// After the wizard saved a new account: create its pane item (sorted into the
        /// account section) and select it, which shows its workspace.
        /// </summary>
        private void OnAccountAdded(AccountWizardViewModel viewModel)
        {
            Account? account = _accountsStore.FindByName(viewModel.Name.Trim());
            if (account is null)
            {
                _log.Error($"Account '{viewModel.Name}' was saved but cannot be found; restart to see it.");
                return;
            }

            NavigationViewItem item = CreateAccountNavItem(account);
            InsertAccountNavItemSorted(item);
            UpdateSyncAllEnabled();
            WorkspaceNav.SelectedItem = item; // SelectionChanged shows the new workspace
        }

        /// <summary>
        /// After the wizard saved an edit: refresh the pane item and drop the cached
        /// workspace — it still runs on the old profile — so the next visit (immediate,
        /// when it is the current one) rebuilds it from the updated account.
        /// </summary>
        private void OnAccountEdited(Guid id)
        {
            Account? account = _accountsStore.GetAll().FirstOrDefault(a => a.Id == id);
            if (account is null)
            {
                return;
            }

            if (_navItems.TryGetValue(id, out NavigationViewItem? item))
            {
                item.Content = account.Name;
                ToolTipService.SetToolTip(item, account.LastSyncSummary ?? "Never synced");
            }

            if (_workspaces.ContainsKey(id))
            {
                EvictWorkspace(id);
                if (_currentWorkspaceId == id)
                {
                    ShowWorkspace(id);
                }
            }
        }

        /// <summary>
        /// The context menu's Delete: confirms by name, then removes the account's
        /// metadata and token, its pane item, and its cached workspace. Clones on disk
        /// stay. Quick Sync takes over when the deleted account was on screen.
        /// </summary>
        private async Task ConfirmDeleteAccountAsync(Guid id)
        {
            Account? account = _accountsStore.GetAll().FirstOrDefault(a => a.Id == id);
            if (account is null)
            {
                return;
            }

            var confirm = new ContentDialog
            {
                Title = "Delete account",
                Content = $"Delete '{account.Name}'? The saved profile and its stored token "
                    + "are removed. Repositories already cloned to disk are not touched.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await DialogGuard.ShowAsync(confirm) != ContentDialogResult.Primary)
            {
                return;
            }

            try
            {
                _accountsStore.Delete(id);
            }
            catch (Exception ex)
            {
                _log.Error($"Deleting account '{account.Name}' failed: {ex.Message}", ex);
                await ShowMessageAsync(
                    "Delete account", $"The account could not be deleted.\n{ex.Message}");
                return;
            }

            EvictWorkspace(id);
            if (_navItems.TryGetValue(id, out NavigationViewItem? item))
            {
                _navItems.Remove(id);
                WorkspaceNav.MenuItems.Remove(item);
            }
            UpdateSyncAllEnabled();

            if (_currentWorkspaceId == id)
            {
                _currentWorkspaceId = Guid.Empty;
                WorkspaceNav.SelectedItem = QuickSyncNavItem; // SelectionChanged shows it
            }
            _log.Info($"Account '{account.Name}' deleted.");
        }

        /// <summary>
        /// Removes a cached workspace: unhooks its badge subscription, cancels any
        /// in-flight sync, disposes the view model, and clears its pane badge. The nav
        /// item itself stays — delete removes it, edit rebuilds the workspace behind it.
        /// </summary>
        private void EvictWorkspace(Guid id)
        {
            if (!_workspaces.TryGetValue(id, out var workspace))
            {
                return;
            }

            if (_badgeHandlers.TryGetValue(id, out PropertyChangedEventHandler? handler))
            {
                workspace.ViewModel.PropertyChanged -= handler;
                _badgeHandlers.Remove(id);
            }
            if (workspace.ViewModel.SyncCancelCommand.CanExecute(null))
            {
                workspace.ViewModel.SyncCancelCommand.Execute(null);
            }
            workspace.ViewModel.Dispose();
            _workspaces.Remove(id);

            if (_navItems.TryGetValue(id, out NavigationViewItem? item))
            {
                item.InfoBadge = null;
            }
        }

        // ---------------------------------------------------------------- sync all

        /// <summary>
        /// The 'Sync all' footer command: runs every account workspace sequentially in
        /// pane order via <see cref="SyncAllCoordinator"/>, then shows a short summary.
        /// A second activation while running cancels the queue after the in-flight
        /// account (that account's own sync is left to finish).
        /// </summary>
        private async Task ToggleSyncAllAsync()
        {
            if (_cancelSyncAll is not null)
            {
                _cancelSyncAll();
                return;
            }

            // Account workspaces in pane order, created (not shown) when never visited.
            var accountWorkspaces = new List<WorkspaceViewModel>();
            foreach (object menuItem in WorkspaceNav.MenuItems)
            {
                if (menuItem is NavigationViewItem { Tag: Guid id }
                    && id != Guid.Empty
                    && EnsureWorkspace(id) is { } workspace)
                {
                    accountWorkspaces.Add(workspace.ViewModel);
                }
            }
            if (accountWorkspaces.Count == 0)
            {
                return;
            }

            using var cts = new CancellationTokenSource();
            _cancelSyncAll = cts.Cancel;
            SyncAllNavItem.Content = "Cancel sync all";
            try
            {
                SyncAllResult result =
                    await new SyncAllCoordinator(_log).RunAsync(accountWorkspaces, cts.Token);

                string accounts = result.Ran == 1 ? "1 account" : $"{result.Ran} accounts";
                string message = result.WasCanceled
                    ? $"Sync all was canceled: {accounts} synced, {result.Skipped} skipped."
                    : $"Sync all finished: {accounts} synced, {result.Skipped} skipped.";
                await ShowMessageAsync("Sync all", message);
            }
            catch (Exception ex)
            {
                // Unexpected only: the coordinator reports per-account trouble by
                // skipping, and DialogGuard turns a blocked summary dialog into a
                // no-op instead of a throw; the activity log carries the
                // per-account outcomes either way.
                _log.Error($"Sync all did not finish cleanly: {ex.Message}", ex);
            }
            finally
            {
                _cancelSyncAll = null;
                SyncAllNavItem.Content = "Sync all";
                UpdateSyncAllEnabled();
            }
        }

        /// <summary>
        /// 'Sync all' is only actionable with at least one account — or while a run is
        /// in flight, when the item must stay enabled so it can be canceled.
        /// </summary>
        private void UpdateSyncAllEnabled()
            => SyncAllNavItem.IsEnabled =
                _cancelSyncAll is not null || _navItems.Keys.Any(id => id != Guid.Empty);

        /// <summary>
        /// Best effort on close: stop the 'Sync all' queue, cancel in-flight git work in
        /// every still-cached workspace (edits and deletes evict entries earlier, so this
        /// covers whatever remains) so clones are not killed mid-checkout (a killed clone
        /// is repaired on the next run), then dispose the view models and drop the badge
        /// subscriptions.
        /// </summary>
        private void DisposeWorkspaces()
        {
            _cancelSyncAll?.Invoke(); // no further accounts start while the window tears down

            foreach ((Guid id, var workspace) in _workspaces)
            {
                if (_badgeHandlers.TryGetValue(id, out PropertyChangedEventHandler? handler))
                {
                    workspace.ViewModel.PropertyChanged -= handler;
                }
                if (workspace.ViewModel.SyncCancelCommand.CanExecute(null))
                {
                    workspace.ViewModel.SyncCancelCommand.Execute(null);
                }
                workspace.ViewModel.Dispose();
            }
            _badgeHandlers.Clear();
            _workspaces.Clear();
        }

        // ---------------------------------------------------------------- menu bar

        private async void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SettingsDialog(
                _settings, _tokenVault, () => WinRT.Interop.WindowNative.GetWindowHandle(this))
            {
                XamlRoot = Content.XamlRoot,
            };
            if (await DialogGuard.ShowAsync(dialog) == ContentDialogResult.Primary)
            {
                dialog.ApplyAndSave();
                ApplySettings();
            }
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => Close();

        private async void ActivityLogMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new LogViewerDialog(_log) { XamlRoot = Content.XamlRoot };
            await DialogGuard.ShowAsync(dialog);
        }

        private async void GitHubMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(RepoUrl));
        }

        private async void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AboutDialog { XamlRoot = Content.XamlRoot };
            await DialogGuard.ShowAsync(dialog);
        }

        private async void CheckForUpdatesMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!_updateService.IsSupported)
            {
                await ShowMessageAsync(
                    "Check for updates",
                    "Updates are only available in installed builds.");
                return;
            }

            var result = await _updateService.CheckAsync();
            if (result.Error is not null)
            {
                await ShowMessageAsync(
                    "Check for updates",
                    $"Could not check for updates.\n{result.Error}");
                return;
            }

            if (result.AvailableVersion is null)
            {
                string current = _updateService.CurrentVersion is string v ? $" (v{v})" : "";
                await ShowMessageAsync("Check for updates", $"You are up to date{current}.");
                return;
            }

            var confirm = new ContentDialog
            {
                Title = "Update available",
                Content = $"gclo v{result.AvailableVersion} is available. "
                    + "The app will restart to finish installing the update.",
                PrimaryButtonText = "Update and restart",
                CloseButtonText = "Not now",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };
            if (await DialogGuard.ShowAsync(confirm) != ContentDialogResult.Primary)
            {
                return;
            }

            // On success this exits the process to restart into the new version,
            // so reaching the line below means the update did not go through.
            string? error = await _updateService.DownloadAndApplyAsync();
            if (error is not null)
            {
                await ShowMessageAsync("Update failed", error);
            }
        }

        private async Task ShowMessageAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot,
            };
            await DialogGuard.ShowAsync(dialog);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
    }
}
