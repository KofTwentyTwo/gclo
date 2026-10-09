/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using gclo.Engine;
using gclo.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;


namespace gclo;


/// <summary>
/// Modal editor for <see cref="AppSettings"/> plus the optional default GitHub
/// token (kept in the <see cref="ITokenVault"/>, never in settings.json). The
/// dialog only edits and persists values; the caller owns side effects like
/// applying the theme.
///
/// WinUI 3 requires <c>XamlRoot</c> to be set before <c>ShowAsync</c>. Typical
/// usage from a Window:
/// <code>
/// var dialog = new SettingsDialog(settings, vault, () => hwnd) { XamlRoot = Content.XamlRoot };
/// if (await dialog.ShowAsync() == ContentDialogResult.Primary)
/// {
///     dialog.ApplyAndSave();
/// }
/// </code>
/// </summary>
public sealed partial class SettingsDialog : ContentDialog
{
   private readonly AppSettings _settings;

   private readonly ITokenVault _vault;

   private readonly IActivityLog _log;

   // A ContentDialog has no HWND of its own; the host window supplies one for pickers.
   private readonly Func<nint> _windowHandleProvider;

   /// <summary>Set by the Remove link; the deletion happens on Save.</summary>
   private bool _removeSavedToken;



   /// <summary>
   /// Creates the dialog and populates the controls from <paramref name="settings"/>;
   /// <paramref name="log"/> records what a save changed (field names and values,
   /// never the token).
   /// </summary>
   public SettingsDialog(AppSettings settings, ITokenVault vault, Func<nint> windowHandleProvider, IActivityLog? log = null)
   {
      ArgumentNullException.ThrowIfNull(settings);
      ArgumentNullException.ThrowIfNull(vault);
      ArgumentNullException.ThrowIfNull(windowHandleProvider);
      _settings = settings;
      _vault = vault;
      _log = log ?? new NullActivityLog();
      _windowHandleProvider = windowHandleProvider;
      InitializeComponent();

      TargetFolderBox.Text = settings.DefaultTargetFolder;
      ConcurrencyBox.Value = settings.DefaultMaxConcurrency;
      ThemeBox.SelectedIndex = settings.Theme switch
      {
         "Light" => 1,
         "Dark" => 2,
         _ => 0,
      };
      SplashToggle.IsOn = settings.ShowSplashScreen;
      SplashDurationBox.Value = settings.SplashMilliseconds;

      // The saved token is never re-materialized into the box — the box stays
      // empty and means "unchanged"; only typing a new value replaces it.
      RefreshTokenState(hasSaved: _vault.TryRetrieve(AppSettings.DefaultTokenVaultId) is not null);
   }



   /// <summary>
   /// Copies the edited values back into the <see cref="AppSettings"/> instance the
   /// dialog was constructed with, persists them, and applies the default-token
   /// change (store or remove) to the vault. Call after <c>ShowAsync</c> returned
   /// <see cref="ContentDialogResult.Primary"/>.
   /// </summary>
   public void ApplyAndSave()
   {
      var changes = new List<string>();

      string folder = TargetFolderBox.Text.Trim();
      if(!string.Equals(folder, _settings.DefaultTargetFolder, StringComparison.Ordinal))
      {
         changes.Add($"default folder '{_settings.DefaultTargetFolder}' -> '{folder}'");
         _settings.DefaultTargetFolder = folder;
      }

      // NumberBox.Value is NaN when the field was cleared; keep the previous value then.
      double value = ConcurrencyBox.Value;
      if(!double.IsNaN(value))
      {
         int concurrency = Math.Clamp(
             (int)Math.Round(value), AppSettings.MinConcurrency, AppSettings.MaxConcurrency);
         if(concurrency != _settings.DefaultMaxConcurrency)
         {
            changes.Add($"default parallelism {_settings.DefaultMaxConcurrency} -> {concurrency}");
            _settings.DefaultMaxConcurrency = concurrency;
         }
      }

      string theme = ThemeBox.SelectedIndex switch
      {
         1 => "Light",
         2 => "Dark",
         _ => "System",
      };
      if(!string.Equals(theme, _settings.Theme, StringComparison.Ordinal))
      {
         changes.Add($"theme {_settings.Theme} -> {theme}");
         _settings.Theme = theme;
      }

      if(SplashToggle.IsOn != _settings.ShowSplashScreen)
      {
         changes.Add($"splash screen {(SplashToggle.IsOn ? "on" : "off")}");
         _settings.ShowSplashScreen = SplashToggle.IsOn;
      }
      double splash = SplashDurationBox.Value;
      if(!double.IsNaN(splash))
      {
         int milliseconds = Math.Clamp(
             (int)Math.Round(splash),
             AppSettings.MinSplashMilliseconds,
             AppSettings.MaxSplashMilliseconds);
         if(milliseconds != _settings.SplashMilliseconds)
         {
            changes.Add($"splash duration {_settings.SplashMilliseconds} -> {milliseconds} ms");
            _settings.SplashMilliseconds = milliseconds;
         }
      }

      _settings.Save();

      string typed = DefaultTokenBox.Password;
      if(typed.Length > 0)
      {
         _vault.Store(AppSettings.DefaultTokenVaultId, typed);
         changes.Add("default token stored");
      }
      else if(_removeSavedToken)
      {
         _vault.Delete(AppSettings.DefaultTokenVaultId);
         changes.Add("default token removed");
      }

      _log.Info(changes.Count == 0
          ? "Settings saved (no changes)."
          : "Settings saved: " + string.Join("; ", changes) + ".");
   }



   private void RemoveTokenButton_Click(object sender, RoutedEventArgs e)
   {
      _removeSavedToken = true;
      DefaultTokenBox.Password = "";
      TokenStateText.Text = "Saved token will be removed on Save.";
      RemoveTokenButton.Visibility = Visibility.Collapsed;
   }



   private void RefreshTokenState(bool hasSaved)
   {
      TokenStateText.Text = hasSaved
          ? "A default token is saved. Leave the box empty to keep it."
          : "No default token saved.";
      RemoveTokenButton.Visibility = hasSaved ? Visibility.Visible : Visibility.Collapsed;
   }



   private async void BrowseButton_Click(object sender, RoutedEventArgs e)
   {
      var picker = new Windows.Storage.Pickers.FolderPicker();
      picker.FileTypeFilter.Add("*"); // required in packaged apps

      WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandleProvider());

      StorageFolder? folder = await picker.PickSingleFolderAsync();
      if(folder is not null)
      {
         TargetFolderBox.Text = folder.Path;
      }
   }
}
