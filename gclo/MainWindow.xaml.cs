using System;
using System.IO;
using System.Threading.Tasks;
using gclo.Engine;
using gclo.Services;
using gclo.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace gclo
{
    /// <summary>
    /// Main (and only) window: sync inputs, the two-phase Load/Sync actions, overall
    /// progress, and the per-repo table.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private const string RepoUrl = "https://github.com/KofTwentyTwo/gclo";

        private readonly AppSettings _settings;
        private readonly UpdateService _updateService = new();

        // Shared with the view model so the log viewer shows the same file the VM writes to.
        private readonly IActivityLog _log;

        public MainViewModel ViewModel { get; }

        public MainWindow()
        {
            _log = new FileActivityLog();
            ViewModel = new MainViewModel(log: _log);
            InitializeComponent();

            ViewModel.AnnouncementRequested += AnnounceToAssistiveTechnology;
            // LiveSetting alone does not announce: XAML never raises LiveRegionChanged
            // automatically, so each StatusText update must raise it in code-behind.
            ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.StatusText))
                {
                    // Enqueued so the binding has pushed the new text before the event.
                    DispatcherQueue.TryEnqueue(RaiseStatusLiveRegionChanged);
                }
            };

            Title = "gclo — Git Clone Large Organizations";

            // The view model stays UI-free: when ResolvePathsCommand needs the user's
            // path-recovery choices, it calls back through here and the window answers
            // with PathRecoveryDialog.
            ViewModel.RecoveryInteraction = ShowPathRecoveryDialogAsync;

            _settings = AppSettings.Load();
            ApplySettings();

            // AppWindow.Resize takes physical pixels; scale by the monitor DPI so the
            // window is the same visual size at 150%/200% display scaling.
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            double scale = GetDpiForWindow(hwnd) / 96.0;
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1000 * scale), (int)(750 * scale)));

            // Best effort: stop in-flight git work when the window closes so clones are
            // not killed mid-checkout (a killed clone is repaired on the next run).
            Closed += (_, _) =>
            {
                if (ViewModel.SyncCancelCommand.CanExecute(null))
                {
                    ViewModel.SyncCancelCommand.Execute(null);
                }
                ViewModel.Dispose();
            };
        }

        private void ApplySettings()
        {
            if (Content is FrameworkElement root)
            {
                root.RequestedTheme = StatusFormat.ToElementTheme(_settings.Theme);
            }
            if (string.IsNullOrWhiteSpace(ViewModel.TargetFolder)
                && !string.IsNullOrWhiteSpace(_settings.DefaultTargetFolder))
            {
                ViewModel.TargetFolder = _settings.DefaultTargetFolder;
            }
            ViewModel.MaxConcurrency = _settings.DefaultMaxConcurrency;
        }

        /// <summary>
        /// Label for the org-subfolder checkbox; names the actual organization once one is chosen.
        /// </summary>
        public string OrgSubfolderLabel(string organization)
            => string.IsNullOrWhiteSpace(organization)
                ? "Create org subfolder"
                : $"Create {organization.Trim()} subfolder";

        /// <summary>
        /// Header text for a sortable table column, with an arrow on the active sort column.
        /// </summary>
        public string SortHeader(string column, string? sortColumn, bool sortDescending)
            => column == sortColumn ? column + (sortDescending ? " ▼" : " ▲") : column;

        /// <summary>
        /// Visible while any repo in the table is Failed. The parameters are not read; they
        /// are the x:Bind re-evaluation triggers — CompletedCount changes whenever a repo
        /// finishes (including failures) and IsRunning changes when a run starts or ends,
        /// which together cover every point where the set of failed items can change.
        /// </summary>
        public Visibility AnyFailedVisibility(int completedCount, bool isRunning)
        {
            foreach (RepoItemViewModel repo in ViewModel.Repos)
            {
                if (repo.Status == SyncStatus.Failed)
                {
                    return Visibility.Visible;
                }
            }
            return Visibility.Collapsed;
        }

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

        private void RaiseStatusLiveRegionChanged()
        {
            var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(StatusTextBlock)
                ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(StatusTextBlock);
            peer?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
        }

        /// <summary>
        /// Raises a UIA notification so screen readers hear per-repo failures. Routed
        /// through the ListView's peer: panels like Grid have no automation peer.
        /// ImportantMostRecent coalesces bursts when many repositories fail at once.
        /// </summary>
        private void AnnounceToAssistiveTechnology(string message)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => AnnounceToAssistiveTechnology(message));
                return;
            }

            var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(RepoListView)
                ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(RepoListView);
            peer?.RaiseNotificationEvent(
                Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationKind.ActionCompleted,
                Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.ImportantMostRecent,
                message,
                "gclo-repo-status");
        }

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

        /// <summary>
        /// Shows <see cref="PathRecoveryDialog"/> for a repository whose checkout was
        /// blocked by Windows-invalid paths. Returns the user's recovery choice, or null
        /// when the dialog was dismissed (or the row carries no path details).
        /// </summary>
        private async Task<PathRecovery?> ShowPathRecoveryDialogAsync(RepoItemViewModel item)
        {
            if (item.InvalidPaths is not { Count: > 0 } paths)
            {
                return null;
            }

            var dialog = new PathRecoveryDialog(item.Name, paths) { XamlRoot = Content.XamlRoot };
            await dialog.ShowAsync();
            return dialog.Result;
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

        // PasswordBox does not support reliable two-way x:Bind on Password;
        // mirror it into the view model by hand.
        private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            ViewModel.Token = ((PasswordBox)sender).Password;
        }

        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*"); // required in packaged apps

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                ViewModel.TargetFolder = folder.Path;
            }
        }

        private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            string root = ViewModel.EffectiveTargetRoot;
            if (Directory.Exists(root))
            {
                await Windows.System.Launcher.LaunchFolderPathAsync(root);
            }
        }
    }
}
