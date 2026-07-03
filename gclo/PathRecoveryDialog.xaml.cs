using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using gclo.Engine;
using Microsoft.UI.Xaml.Controls;

namespace gclo
{
    /// <summary>
    /// Asks the user how to recover a repository whose tree contains Windows-invalid
    /// paths: rename each offending name (prefilled with the validator's suggestion when
    /// there is one) or skip the file or folder entirely. <see cref="Result"/> carries
    /// the chosen <see cref="PathRecovery"/> after "Apply and check out" and stays null
    /// when the dialog is dismissed ("Skip this repo").
    ///
    /// As with every ContentDialog, the caller must set <c>XamlRoot</c> before
    /// <c>ShowAsync</c>.
    /// </summary>
    public sealed partial class PathRecoveryDialog : ContentDialog
    {
        private readonly List<PathRecoveryRow> _rows;

        /// <summary>The user's recovery choice, or null when the dialog was dismissed.</summary>
        public PathRecovery? Result { get; private set; }

        public PathRecoveryDialog(string repoName, IReadOnlyList<InvalidPathInfo> paths)
        {
            ArgumentNullException.ThrowIfNull(repoName);
            ArgumentNullException.ThrowIfNull(paths);
            InitializeComponent();

            IntroText.Text =
                $"'{repoName}' contains paths that are legal in git but cannot be created on Windows. "
                + "Edit the replacement name for each entry, or mark it Skip to leave it out of the checkout.";

            _rows = new List<PathRecoveryRow>(paths.Count);
            foreach (InvalidPathInfo path in paths)
            {
                _rows.Add(new PathRecoveryRow(path));
            }
            RowsControl.ItemsSource = _rows;
        }

        private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            // Git paths are case-sensitive, so both collections compare ordinally. The same
            // repo path can appear in two rows (e.g. an invalid segment that also collides
            // by case), which is why renames assigns via the indexer instead of Add.
            var renames = new Dictionary<string, string>(StringComparer.Ordinal);
            var skipped = new HashSet<string>(StringComparer.Ordinal);

            foreach (PathRecoveryRow row in _rows)
            {
                if (row.Skip)
                {
                    skipped.Add(row.RepoPath);
                }
                else if (row.BuildReplacementPath() is { } replacement)
                {
                    renames[row.RepoPath] = replacement;
                }
            }

            Result = new PathRecovery(renames, skipped);
        }
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
