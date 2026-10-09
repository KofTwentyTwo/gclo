using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using gclo.Engine;
using gclo.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace gclo
{
    /// <summary>
    /// Asks the user how to recover a repository whose tree contains Windows-invalid
    /// paths: rename each offending name (prefilled with the validator's suggestion when
    /// there is one) or skip the file or folder entirely. Rows the validator has no safe
    /// rename for (case-only collisions, duplicate destinations) start as Skip — the
    /// same default the CLI's --sanitize-paths applies — so accepting the defaults
    /// never produces a plan that is guaranteed to fail (#30). "Apply and check out"
    /// pre-flights the choices through <see cref="PathRecoveryPlanner"/> and keeps the
    /// dialog open with the problems listed when they would fail. <see cref="Result"/>
    /// carries the chosen <see cref="PathRecovery"/> after a sound apply and stays null
    /// when the dialog is dismissed ("Skip this repo"). When WSL can run git on this
    /// machine the dialog also offers "Clone in WSL instead" (#8), which skips the
    /// Windows checkout entirely: <see cref="Decision"/> then reports that choice.
    ///
    /// As with every ContentDialog, the caller must set <c>XamlRoot</c> before
    /// <c>ShowAsync</c>.
    /// </summary>
    public sealed partial class PathRecoveryDialog : ContentDialog
    {
        private readonly List<PathRecoveryRow> _rows;

        /// <summary>The user's recovery choice, or null when the dialog was dismissed.</summary>
        public PathRecoveryDecision? Decision { get; private set; }

        /// <summary>The chosen renames/skips when <see cref="Decision"/> is an apply; null otherwise.</summary>
        public PathRecovery? Result => (Decision as PathRecoveryDecision.Apply)?.Recovery;

        /// <param name="wslAvailable">Offers the "Clone in WSL instead" button (see <see cref="WorkspaceViewModel.IsWslCloneAvailable"/>).</param>
        public PathRecoveryDialog(string repoName, IReadOnlyList<InvalidPathInfo> paths, bool wslAvailable = false)
        {
            ArgumentNullException.ThrowIfNull(repoName);
            ArgumentNullException.ThrowIfNull(paths);
            InitializeComponent();

            IntroText.Text =
                $"'{repoName}' contains paths that are legal in git but cannot be created on Windows. "
                + "Edit the replacement name for each entry, or mark it Skip to leave it out of the checkout."
                + (wslAvailable
                    ? " Or clone the repository unchanged inside WSL (~/gclo/<org>/<repo> in your default distribution), where these paths are legal."
                    : "");
            if (wslAvailable)
            {
                SecondaryButtonText = "Clone in WSL instead";
            }

            _rows = new List<PathRecoveryRow>(paths.Count);
            foreach (InvalidPathInfo path in paths)
            {
                _rows.Add(new PathRecoveryRow(path));
            }
            RowsControl.ItemsSource = _rows;
        }

        private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            PathRecoveryPlanner.Plan plan = PathRecoveryPlanner.Build(
                _rows.Select(row => new PathRecoveryPlanner.Choice(row.RepoPath, row.BuildReplacementPath(), row.Skip))
                    .ToList());

            if (plan.Problems.Count > 0)
            {
                // Stay open and say which rows need a different choice.
                args.Cancel = true;
                ProblemsBar.Message = string.Join(Environment.NewLine, plan.Problems);
                ProblemsBar.IsOpen = true;
                return;
            }

            ProblemsBar.IsOpen = false;
            Decision = new PathRecoveryDecision.Apply(plan.Recovery);
        }

        private void OnSecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
            => Decision = new PathRecoveryDecision.CloneInWsl();
    }

    /// <summary>
    /// One offending path in <see cref="PathRecoveryDialog"/>: the original repo path and
    /// reason (read-only), plus the user's replacement name for the offending segment and
    /// the per-row Skip choice. <see cref="InvalidPathInfo.RepoPath"/> always ends with
    /// the offending segment, so the rename applies to the last segment only.
    /// </summary>
    public sealed partial class PathRecoveryRow : ObservableObject
    {
        /// <summary>Repo path up to the offending segment: "" or "dir/sub/" (trailing slash).</summary>
        private readonly string _parentPrefix;

        /// <summary>The offending (last) segment of <see cref="RepoPath"/>.</summary>
        private readonly string _originalSegment;

        /// <summary>Initial TextBox value: the validator's suggestion, or the original segment.</summary>
        private readonly string _prefill;

        internal PathRecoveryRow(InvalidPathInfo info)
        {
            RepoPath = info.RepoPath;
            Reason = info.Reason;
            int slash = info.RepoPath.LastIndexOf('/');
            _parentPrefix = slash < 0 ? "" : info.RepoPath[..(slash + 1)];
            _originalSegment = info.RepoPath[(slash + 1)..];
            _prefill = info.SuggestedName ?? _originalSegment;
            NewName = _prefill;
            // No safe rename exists (case-only collision, duplicate destination): the
            // original name would fail again, so the row defaults to Skip.
            Skip = info.SuggestedName is null;
        }

        /// <summary>Full repo path (forward slashes) of the offending file or folder.</summary>
        public string RepoPath { get; }

        /// <summary>Why the path cannot be created on Windows.</summary>
        public string Reason { get; }

        /// <summary>Replacement name for the offending segment, edited by the user.</summary>
        [ObservableProperty]
        public partial string NewName { get; set; }

        /// <summary>When set, the whole file or folder is omitted from the checkout.</summary>
        [ObservableProperty]
        public partial bool Skip { get; set; }

        /// <summary>The rename box is only editable while the row is not skipped.</summary>
        public bool RenameEnabled => !Skip;

        partial void OnSkipChanged(bool value) => OnPropertyChanged(nameof(RenameEnabled));

        /// <summary>
        /// Full replacement repo path for a non-skipped row, or null when the name is
        /// unchanged and no rename is needed. An emptied TextBox falls back to the
        /// prefill; the engine re-validates the effective path set on apply, so a
        /// still-invalid manual edit surfaces as a fresh set of invalid paths.
        /// </summary>
        internal string? BuildReplacementPath()
        {
            string segment = string.IsNullOrWhiteSpace(NewName) ? _prefill : NewName;
            return segment == _originalSegment ? null : _parentPrefix + segment;
        }
    }
}
