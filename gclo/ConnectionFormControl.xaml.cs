/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System;
using gclo.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;


namespace gclo;


/// <summary>
/// The connection form (token, organization, target folder, org subfolder, path
/// preview) over a <see cref="WorkspaceViewModel"/>. One definition for the connect
/// card and the chip's Edit flyout, which used to carry diverging copies (#31).
/// <see cref="AutomationIdPrefix"/> gives each host its own UIA ids
/// ("ConnectTokenBox", "EditTokenBox", ...).
/// </summary>
public sealed partial class ConnectionFormControl : UserControl
{
   /// <summary>The workspace the form edits; set by the host before the control loads.</summary>
   public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
       nameof(ViewModel), typeof(WorkspaceViewModel), typeof(ConnectionFormControl), new PropertyMetadata(null));

   /// <summary>Prefix for the form's AutomationIds, e.g. "Connect" or "Edit".</summary>
   public static readonly DependencyProperty AutomationIdPrefixProperty = DependencyProperty.Register(
       nameof(AutomationIdPrefix), typeof(string), typeof(ConnectionFormControl), new PropertyMetadata(""));



   public ConnectionFormControl()
   {
      InitializeComponent();
   }



   /// <summary>The workspace state this form edits; target of every x:Bind.</summary>
   public WorkspaceViewModel ViewModel
   {
      get => (WorkspaceViewModel)GetValue(ViewModelProperty);
      set => SetValue(ViewModelProperty, value);
   }



   /// <summary>Prefix for the form's AutomationIds, e.g. "Connect" or "Edit".</summary>
   public string AutomationIdPrefix
   {
      get => (string)GetValue(AutomationIdPrefixProperty);
      set => SetValue(AutomationIdPrefixProperty, value);
   }



   /// <summary>
   /// Supplies the host window's HWND for the folder picker (a UserControl has no
   /// HWND of its own). Set by the host; Browse is a no-op without it.
   /// </summary>
   public Func<nint>? WindowHandleProvider { get; set; }



   /// <summary>Composes an AutomationId from the host's prefix.</summary>
   public string Id(string suffix) => AutomationIdPrefix + suffix;



   /// <summary>Label for the org-subfolder checkbox; names the actual organization once one is chosen.</summary>
   public string OrgSubfolderLabel(string organization)
       => string.IsNullOrWhiteSpace(organization)
           ? "Create org subfolder"
           : $"Create {organization.Trim()} subfolder";



   // Runs on every host load (the card once, the flyout on each open). A token the
   // view model already holds (an account's token, or the saved default token) is
   // deliberately NOT mirrored into the box: the box shows the same fixed mask as
   // Settings, so it reads as "a token is in effect" (an empty box read as "no
   // token", #101), and an untouched mask keeps it. Only typing replaces (#32).
   private void OnLoaded(object sender, RoutedEventArgs e)
   {
      bool hasToken = ViewModel is not null && ViewModel.Token.Length > 0;
      TokenBox.Password = hasToken ? SettingsTokenState.Mask : "";
      TokenBox.PlaceholderText = "ghp_…";
      // The organization placeholder must not ask for a token the box already has.
      OrgBox.PlaceholderText = hasToken ? "Choose an organization, or type one" : "Paste a token first, or type an org";
      ToolTipService.SetToolTip(TokenBox, !hasToken
          ? "Fine-grained or classic PAT with repository read access; used for the GitHub API and git over HTTPS. Never written to disk or logs."
          : ViewModel!.AccountId is null
              ? "Using the saved default token from Settings. Type here to use a different one for this session."
              : "Using this account's token. Type here to use a different one for this session.");
   }



   // PasswordBox does not support reliable two-way x:Bind on Password; mirror it
   // into the view model by hand. The mask is not a token: while the box shows it,
   // the view model keeps the token it already holds.
   private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e)
   {
      string typed = ((PasswordBox)sender).Password;
      if(ViewModel is not null && !string.Equals(typed, SettingsTokenState.Mask, StringComparison.Ordinal))
      {
         ViewModel.Token = typed;
      }
   }



   // An editable ComboBox does not render Text that was set before its template
   // loaded, so an account workspace's seeded org would show as blank even though
   // the view model holds it — see EditableComboBox.
   private void OrgBox_Loaded(object sender, RoutedEventArgs e)
   {
      if(ViewModel is not null)
      {
         EditableComboBox.ReapplyText((ComboBox)sender, ViewModel.Organization);
      }
   }



   private async void BrowseButton_Click(object sender, RoutedEventArgs e)
   {
      if(ViewModel is null || WindowHandleProvider is not { } handle)
      {
         return;
      }

      var picker = new Windows.Storage.Pickers.FolderPicker();
      picker.FileTypeFilter.Add("*"); // required in packaged apps

      WinRT.Interop.InitializeWithWindow.Initialize(picker, handle());

      StorageFolder? folder = await picker.PickSingleFolderAsync();
      if(folder is not null)
      {
         ViewModel.TargetFolder = folder.Path;
      }
   }
}
