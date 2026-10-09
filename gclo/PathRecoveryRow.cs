/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using gclo.Engine;


namespace gclo;


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
      return string.Equals(segment, _originalSegment, StringComparison.Ordinal) ? null : _parentPrefix + segment;
   }
}
