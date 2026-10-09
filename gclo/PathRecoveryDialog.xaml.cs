/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.Linq;
using gclo.Engine;
using gclo.ViewModels;
using Microsoft.UI.Xaml.Controls;


namespace gclo;


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



   /// <param name="repoName"></param>

   /// <param name="paths"></param>   /// <param name="wslAvailable">Offers the "Clone in WSL instead" button (see <see cref="WorkspaceViewModel.IsWslCloneAvailable"/>).</param>
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
      if(wslAvailable)
      {
         SecondaryButtonText = "Clone in WSL instead";
      }

      _rows = new List<PathRecoveryRow>(paths.Count);
      foreach(InvalidPathInfo path in paths)
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

      if(plan.Problems.Count > 0)
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
