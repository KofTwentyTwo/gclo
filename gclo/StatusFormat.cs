/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.Engine;
using gclo.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;


namespace gclo;


/// <summary>Static helpers for x:Bind function bindings in WorkspacePage.xaml.</summary>
public static class StatusFormat
{
   /// <summary>
   /// The theme the window actually shows, kept current by MainWindow. Function-bound
   /// brushes resolve against it: Application.Current.Resources follows the
   /// application theme fixed at startup, so after an in-session switch (or an OS
   /// flip in System mode) it would keep handing out the old theme's colors (#31).
   /// </summary>
   public static ElementTheme CurrentTheme { get; set; } = ElementTheme.Default;



   /// <summary>
   /// Theme- and high-contrast-aware brush for a status. Resolved from the theme
   /// dictionary matching <see cref="CurrentTheme"/> on every call; x:Bind
   /// re-invokes this whenever the bound Status changes, and MainWindow asks every
   /// row to re-evaluate when the theme changes.
   /// </summary>
   public static Brush BrushFor(SyncStatus status) => ResourceBrush(status switch
   {
      SyncStatus.Cloning or SyncStatus.Pulling => "SystemFillColorAttentionBrush",
      SyncStatus.Done => "SystemFillColorSuccessBrush",
      SyncStatus.Failed => "SystemFillColorCriticalBrush",
      SyncStatus.Canceled => "SystemFillColorCautionBrush",
      _ => "TextFillColorSecondaryBrush",
   });



   /// <summary>
   /// Segoe Fluent Icons glyph for a status, shown beside the status text so state
   /// is never communicated by color alone.
   /// </summary>
   public static string GlyphFor(SyncStatus status) => status switch
   {
      SyncStatus.Queued => "",   // Recent (clock)
      SyncStatus.Cloning => "",  // Download
      SyncStatus.Pulling => "",  // Sync
      SyncStatus.Done => "",     // CheckMark
      SyncStatus.Failed => "",   // Error
      SyncStatus.Canceled => "", // Cancel
      _ => "",
   };



   /// <summary>Screen-reader name for a row's path-recovery link.</summary>
   public static string ResolveAutomationName(string repoName)
       => $"Resolve invalid paths for {repoName}";



   /// <summary>A brush resource resolved for <see cref="CurrentTheme"/> (see <see cref="BrushFor"/>).</summary>
   public static Brush ThemedBrush(string key) => ResourceBrush(key);



   private static Brush ResourceBrush(string key)
   {
      // Explicit Light/Dark: read the matching theme dictionary directly. The WinUI
      // control resources merge their brushes under "Light" and "Default" (dark);
      // high contrast and Default fall back to the application-theme lookup.
      string? dictionary = CurrentTheme switch
      {
         ElementTheme.Light => "Light",
         ElementTheme.Dark => "Default",
         _ => null,
      };
      if(dictionary is not null && TryFindThemed(Application.Current.Resources, dictionary, key, out Brush? themed))
      {
         return themed;
      }

      return Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush
          ? brush
          // Unreachable with XamlControlsResources merged; a visible-but-neutral
          // fallback beats crashing the row template if a key ever disappears.
          : new SolidColorBrush(Colors.Gray);
   }



   private static bool TryFindThemed(
       ResourceDictionary resources, string theme, string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Brush? brush)
   {
      if(resources.ThemeDictionaries.TryGetValue(theme, out object? themed)
          && themed is ResourceDictionary themeDictionary
          && themeDictionary.TryGetValue(key, out object? value)
          && value is Brush found)
      {
         brush = found;
         return true;
      }
      foreach(ResourceDictionary merged in resources.MergedDictionaries)
      {
         if(TryFindThemed(merged, theme, key, out brush))
         {
            return true;
         }
      }
      brush = null;
      return false;
   }



   public static Visibility VisibleIf(bool value)
       => value ? Visibility.Visible : Visibility.Collapsed;



   /// <summary>Inverse of <see cref="VisibleIf"/>: collapsed when <paramref name="value"/> is true.</summary>
   public static Visibility VisibleIfNot(bool value)
       => value ? Visibility.Collapsed : Visibility.Visible;



   /// <summary>Visible only once <paramref name="value"/> has text; hides empty preview lines.</summary>
   public static Visibility VisibleIfNotEmpty(string value)
       => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;



   /// <summary>InfoBar severity for a run outcome (None and Canceled read as informational).</summary>
   public static InfoBarSeverity SeverityFor(RunResultKind kind) => kind switch
   {
      RunResultKind.Success => InfoBarSeverity.Success,
      RunResultKind.PartialFailure => InfoBarSeverity.Warning,
      RunResultKind.Error => InfoBarSeverity.Error,
      _ => InfoBarSeverity.Informational, // Canceled, None
   };



   /// <summary>Short InfoBar title for a run outcome; empty for <see cref="RunResultKind.None"/>.</summary>
   public static string TitleFor(RunResultKind kind) => kind switch
   {
      RunResultKind.Success => "Sync complete",
      RunResultKind.PartialFailure => "Completed with failures",
      RunResultKind.Canceled => "Canceled",
      RunResultKind.Error => "Sync failed",
      _ => "",
   };



   /// <summary>Repository count with explicit pluralization, e.g. "87 repositories".</summary>
   public static string RepoCountText(int count)
       => count == 1 ? "1 repository" : $"{count} repositories";



   /// <summary>Toolbar selection summary, e.g. "12 of 87 selected".</summary>
   public static string SelectionSummary(int selected, int total)
       => $"{selected} of {total} selected";



   /// <summary>Run progress shown beside the toolbar's progress bar, e.g. "34 of 87".</summary>
   public static string RunProgressText(int completed, int total)
       => $"{completed} of {total}";



   /// <summary>Screen-reader name for a repository's progress bar in the active strip.</summary>
   public static string ProgressAutomationName(string repoName)
       => $"{repoName} sync progress";



   /// <summary>Maps the persisted theme string to a XAML theme ("System"/unknown => Default).</summary>
   public static ElementTheme ToElementTheme(string theme) => theme switch
   {
      "Light" => ElementTheme.Light,
      "Dark" => ElementTheme.Dark,
      _ => ElementTheme.Default,
   };
}
