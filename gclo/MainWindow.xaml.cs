using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
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

        /// <summary>The workspace the pane currently rests on; where 'Add account' reverts to.</summary>
        private Guid _currentWorkspaceId = Guid.Empty;

        /// <summary>Suppresses re-entrant SelectionChanged while the code puts a selection back.</summary>
        private bool _revertingSelection;

        public MainWindow()
        {
            _log = new FileActivityLog();
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
                var item = new NavigationViewItem
                {
                    Content = account.Name,
                    Tag = account.Id,
                    Icon = new FontIcon { Glyph = "" },
                };
                ToolTipService.SetToolTip(item, account.LastSyncSummary ?? "Never synced");
                _navItems[account.Id] = item;
                WorkspaceNav.MenuItems.Add(item);
            }

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
                // The footer entry is a command, not a destination: put the selection
                // back on the current workspace, then show the stub dialog.
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
                await ShowAddAccountStubAsync();
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
        /// Puts the workspace for <paramref name="id"/> on screen, creating and caching
        /// it on first visit. Cached view models keep running syncs alive across switches.
        /// </summary>
        private void ShowWorkspace(Guid id)
        {
            if (!_workspaces.TryGetValue(id, out var workspace))
            {
                WorkspaceViewModel? viewModel = CreateWorkspaceViewModel(id);
                if (viewModel is null)
                {
                    return; // stale item: the account no longer exists in the store
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
            }

            WorkspaceNav.Content = workspace.Page;
        }

        private WorkspaceViewModel? CreateWorkspaceViewModel(Guid id)
        {
            if (id == Guid.Empty)
            {
                // Quick Sync: no account behind it — exactly the pre-accounts behavior.
                return new WorkspaceViewModel(log: _log);
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

        private async Task ShowAddAccountStubAsync()
        {
            var dialog = new ContentDialog
            {
                Title = "Add account",
                Content = "The account setup wizard arrives in the next update. "
                    + "Accounts created by future versions appear here automatically.",
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }

        /// <summary>
        /// Best effort on close: cancel in-flight git work in every cached workspace so
        /// clones are not killed mid-checkout (a killed clone is repaired on the next
        /// run), then dispose the view models and drop the badge subscriptions.
        /// </summary>
        private void DisposeWorkspaces()
        {
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
            var dialog = new SettingsDialog(_settings) { XamlRoot = Content.XamlRoot };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                dialog.ApplyAndSave();
                ApplySettings();
            }
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => Close();

        private async void ActivityLogMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new LogViewerDialog(_log) { XamlRoot = Content.XamlRoot };
            await dialog.ShowAsync();
        }

        private async void GitHubMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(RepoUrl));
        }

        private async void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AboutDialog { XamlRoot = Content.XamlRoot };
            await dialog.ShowAsync();
        }

        private async void CheckForUpdatesMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!_updateService.IsSupported)
            {
                await ShowUpdateMessageAsync(
                    "Check for updates",
                    "Updates are only available in installed builds.");
                return;
            }

            var result = await _updateService.CheckAsync();
            if (result.Error is not null)
            {
                await ShowUpdateMessageAsync(
                    "Check for updates",
                    $"Could not check for updates.\n{result.Error}");
                return;
            }

            if (result.AvailableVersion is null)
            {
                string current = _updateService.CurrentVersion is string v ? $" (v{v})" : "";
                await ShowUpdateMessageAsync("Check for updates", $"You are up to date{current}.");
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
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            // On success this exits the process to restart into the new version,
            // so reaching the line below means the update did not go through.
            string? error = await _updateService.DownloadAndApplyAsync();
            if (error is not null)
            {
                await ShowUpdateMessageAsync("Update failed", error);
            }
        }

        private async Task ShowUpdateMessageAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
    }
}
