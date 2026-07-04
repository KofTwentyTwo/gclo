using System;
using System.IO;
using System.Threading.Tasks;
using gclo.Engine;
using gclo.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace gclo
{
    /// <summary>
    /// One sync workspace: inputs, the two-phase Load/Sync actions, overall progress,
    /// and the per-repo table. Hosted by <see cref="MainWindow"/>'s navigation shell —
    /// one instance per saved account plus one for the pinned Quick Sync entry. Pages
    /// (and their view models) are cached by the shell, so a running sync keeps going
    /// while another workspace is displayed.
    /// </summary>
    public sealed partial class WorkspacePage : UserControl
    {
        // A UserControl has no HWND of its own; the host window supplies one for pickers.
        private readonly Func<nint> _windowHandleProvider;

        /// <summary>The workspace state this page renders; target of every x:Bind.</summary>
        public WorkspaceViewModel ViewModel { get; }

        /// <summary>
        /// Wires the page to its (caller-owned) view model. The
        /// <paramref name="windowHandleProvider"/> returns the host window's HWND,
        /// needed to initialize the folder picker.
        /// </summary>
        public WorkspacePage(WorkspaceViewModel viewModel, Func<nint> windowHandleProvider)
        {
            ArgumentNullException.ThrowIfNull(viewModel);
            ArgumentNullException.ThrowIfNull(windowHandleProvider);
            ViewModel = viewModel;
            _windowHandleProvider = windowHandleProvider;
            InitializeComponent();

            ViewModel.AnnouncementRequested += AnnounceToAssistiveTechnology;
            // LiveSetting alone does not announce: XAML never raises LiveRegionChanged
            // automatically, so each StatusText update must raise it in code-behind.
            ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(WorkspaceViewModel.StatusText))
                {
                    // Enqueued so the binding has pushed the new text before the event.
                    DispatcherQueue.TryEnqueue(RaiseStatusLiveRegionChanged);
                }
            };

            // The view model stays UI-free: when ResolvePathsCommand needs the user's
            // path-recovery choices, it calls back through here and the page answers
            // with PathRecoveryDialog.
            ViewModel.RecoveryInteraction = ShowPathRecoveryDialogAsync;

            // An account-seeded view model already carries its vault token; mirror it
            // into the box so the UI shows what is in effect. The resulting
            // PasswordChanged echoes the same value back, which the Token setter
            // ignores as a no-op.
            if (ViewModel.Token.Length > 0)
            {
                TokenBox.Password = ViewModel.Token;
            }
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

            var dialog = new PathRecoveryDialog(item.Name, paths) { XamlRoot = XamlRoot };
            await dialog.ShowAsync();
            return dialog.Result;
        }

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

            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandleProvider());

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
