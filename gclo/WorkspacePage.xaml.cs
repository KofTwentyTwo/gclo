using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using gclo.Engine;
using gclo.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace gclo
{
    /// <summary>
    /// One sync workspace in two visual states: a centered connect card until the first
    /// successful load, then a connection chip plus the hero repository table with its
    /// attached toolbar (filter, options, refresh, sync), the pinned active strip, and
    /// the results InfoBar. Hosted by <see cref="MainWindow"/>'s navigation shell —
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
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;

            // The chip's repo count, the toolbar's selection summary, and the
            // empty-filter placeholder are set from code: they derive from collection
            // counts, which raise no property change an x:Bind function could ride on.
            ViewModel.Repos.CollectionChanged += (_, _) => UpdateDerivedTexts();
            ViewModel.FilteredRepos.CollectionChanged += (_, _) => UpdateDerivedTexts();

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

            // SelectorBar starts with no selection; the view model's filter default is
            // All, so select that item (the resulting SelectionChanged is a no-op set).
            FilterSelectorBar.SelectedItem = FilterAllItem;
            UpdateDerivedTexts();
        }

        /// <summary>
        /// Label for the org-subfolder checkbox; names the actual organization once one is chosen.
        /// </summary>
        public string OrgSubfolderLabel(string organization)
            => string.IsNullOrWhiteSpace(organization)
                ? "Create org subfolder"
                : $"Create {organization.Trim()} subfolder";

        /// <summary>True when this workspace is backed by a saved account (vs Quick Sync).</summary>
        public bool IsAccountWorkspace => ViewModel.AccountId is not null;

        /// <summary>
        /// Whether the edit flyout's connection fields accept input: Quick Sync follows
        /// <see cref="WorkspaceViewModel.CanEditInputs"/>; account workspaces are always
        /// read-only here (their settings are edited in the account wizard).
        /// </summary>
        public bool ConnectFieldsEnabled(bool canEditInputs)
            => canEditInputs && ViewModel.AccountId is null;

        /// <summary>
        /// Sort-direction arrow (visual only; the header button's automation name
        /// carries the state) for the active sort column: chevron up = ascending.
        /// </summary>
        public string SortGlyph(string column, string? sortColumn, bool sortDescending)
            => column == sortColumn ? (sortDescending ? "" : "") : "";

        /// <summary>Shows the sort arrow only on the column the table is sorted by.</summary>
        public Visibility SortGlyphVisibility(string column, string? sortColumn)
            => column == sortColumn ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Composed automation name for a sortable column header, e.g.
        /// "Name, sorted ascending" or "Branch, not sorted".
        /// </summary>
        public string SortAutomationName(string column, string? sortColumn, bool sortDescending)
            => column != sortColumn
                ? $"{column}, not sorted"
                : sortDescending
                    ? $"{column}, sorted descending"
                    : $"{column}, sorted ascending";

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WorkspaceViewModel.StatusText))
            {
                // LiveSetting alone does not announce: XAML never raises
                // LiveRegionChanged automatically, so each StatusText update must raise
                // it here. Enqueued so the binding has pushed the new text first.
                DispatcherQueue.TryEnqueue(RaiseStatusLiveRegionChanged);
            }
            else if (e.PropertyName == nameof(WorkspaceViewModel.SelectedCount))
            {
                UpdateDerivedTexts();
            }
        }

        /// <summary>
        /// Refreshes the count-derived texts: the chip's repository count, the
        /// toolbar's "N of M selected" summary, and the empty-filter placeholder.
        /// </summary>
        private void UpdateDerivedTexts()
        {
            ChipRepoCountText.Text = StatusFormat.RepoCountText(ViewModel.Repos.Count);
            SelectionSummaryText.Text =
                StatusFormat.SelectionSummary(ViewModel.SelectedCount, ViewModel.Repos.Count);
            EmptyFilterText.Visibility =
                ViewModel.FilteredRepos.Count == 0 && ViewModel.Repos.Count > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        private void FilterSelectorBar_SelectionChanged(
            SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            if (sender.SelectedItem?.Tag is string tag && Enum.TryParse(tag, out RepoFilter filter))
            {
                ViewModel.Filter = filter;
            }
        }

        // The chip's Edit link opens its attached flyout (HyperlinkButton has no
        // Flyout property of its own).
        private void EditButton_Click(object sender, RoutedEventArgs e)
            => FlyoutBase.ShowAttachedFlyout((FrameworkElement)sender);

        // PasswordBox has no reliable two-way binding, so the flyout's token box is
        // synchronized on open; it then always shows the token in effect.
        private void EditFlyout_Opening(object? sender, object e)
        {
            if (EditTokenBox.Password != ViewModel.Token)
            {
                EditTokenBox.Password = ViewModel.Token;
            }
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
        // mirror it into the view model by hand (shared by the connect card's box
        // and the edit flyout's box).
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
