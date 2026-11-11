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
      bool savedWithAnAccount = ViewModel?.AccountId is not null;
      TokenBox.Password = hasToken ? SettingsTokenState.Mask : "";

      ///////////////////////////////////////////////////////////////////////////////
      // An account's token is read from where it is saved each time it is used    //
      // (#158). A token typed here would look taken and then not be used, so the  //
      // box of an account takes none; the page says where the token is changed    //
      // (#182).                                                                   //
      ///////////////////////////////////////////////////////////////////////////////
      TokenBox.IsEnabled = !savedWithAnAccount;

      ApplyThePlace();
      if(PlaceGitHub.GroupName.Length == 0)
      {
         // A group of this form's own: the connect card and the Edit flyout each host one.
         PlaceGitHub.GroupName = PlaceGitea.GroupName = "gclo-place-" + Guid.NewGuid().ToString("N");
      }
      ShowThePlace();
      if(ViewModel is not null && !ReferenceEquals(_followed, ViewModel))
      {
         ///////////////////////////////////////////////////////////////////////////
         // Quick Sync's place is chosen on this form (#209). What the form says  //
         // about the token follows it, and so does the token box: the view model //
         // empties the token when another place is chosen, and fills in a saved  //
         // default; the box is mirrored by hand, so it is brought along here.     //
         ///////////////////////////////////////////////////////////////////////////
         _followed = ViewModel;
         ViewModel.PropertyChanged += (_, args) =>
         {
            switch(args.PropertyName)
            {
               case nameof(WorkspaceViewModel.Token):
                  FollowTheToken();
                  break;
               case nameof(WorkspaceViewModel.PlaceIndex):
                  ShowThePlace();
                  break;
               case nameof(WorkspaceViewModel.TokenHeader) or nameof(WorkspaceViewModel.TokenPage) or nameof(WorkspaceViewModel.TokenPlaceholder)
                   or nameof(WorkspaceViewModel.IsPlaceChosen):
                  ApplyThePlace();
                  break;
            }
         };
      }
      // The organization placeholder must not ask for a token the box already has, or cannot take.
      OrgBox.PlaceholderText = ViewModel?.CanChoosePlace == true ? "Type a name, or press Find to list them"
          : hasToken ? "Choose an organization, or type one"
          : savedWithAnAccount ? "Type an organization or account"
          : "Paste a token first, or type an org";
      ToolTipService.SetToolTip(TokenBox, savedWithAnAccount
          ? ViewModel!.UsesDefaultToken
              ? "This account uses the default token from File > Settings."
              : "This account's token is set with Edit account…, not typed here."
          : !hasToken
              ? "Needs read access to repositories. Sent only when you press Find or Load repositories, and only to the place chosen above. Never written to disk or logs."
              : "Using the saved default token from Settings. Type here to use a different one for this session.");
   }



   /// <summary>The view model whose place and token this control already follows.</summary>
   private WorkspaceViewModel? _followed;

   /// <summary>True while <see cref="ShowThePlace"/> checks the radio buttons itself: that is not the user choosing.</summary>
   private bool _showingThePlace;



   /// <summary>
   /// Checks the radio button of the place the view model is on, and neither while
   /// no place was chosen.
   /// </summary>
   private void ShowThePlace()
   {
      if(ViewModel is null)
      {
         return;
      }

      _showingThePlace = true;
      try
      {
         int index = ViewModel.PlaceIndex;
         PlaceGitHub.IsChecked = index == 0;
         PlaceGitea.IsChecked = index == 1;
      }
      finally
      {
         _showingThePlace = false;
      }
   }



   /// <summary>
   /// A radio button was checked by the user (click, Space, an arrow key, or UI
   /// automation): that is the choice of the place. Focus alone checks nothing. If
   /// the view model does not take the choice (the workspace is busy), the buttons
   /// go back to what it is on.
   /// </summary>
   private void Place_Checked(object sender, RoutedEventArgs e)
   {
      if(_showingThePlace || ViewModel is null)
      {
         return;
      }

      ViewModel.PlaceIndex = ReferenceEquals(sender, PlaceGitea) ? 1 : 0;
      ShowThePlace();
   }



   /// <summary>
   /// What the form says about the token of the place the workspace is on: the
   /// header of the box, what the empty box shows, and the page a token is made on
   /// (none when the place is not known).
   /// </summary>
   private void ApplyThePlace()
   {
      if(ViewModel is null)
      {
         return;
      }

      // No token is taken before the user said where the repositories are; an account's box takes none at all (#182).
      TokenBox.IsEnabled = ViewModel.AccountId is null && ViewModel.IsPlaceChosen;
      TokenBox.Header = ViewModel.TokenHeader;
      TokenPageLink.NavigateUri = ViewModel.TokenPage;
      TokenPageLink.Visibility = StatusFormat.VisibleIfNotNull(ViewModel.TokenPage);
      TokenBox.PlaceholderText = ViewModel.AccountId is null ? ViewModel.TokenPlaceholder
          : ViewModel.UsesDefaultToken ? "No default token is saved"
          : "No token is saved for this account";
   }



   /// <summary>
   /// Brings the token box along when the view model changed the token itself:
   /// emptied (another place was chosen), or filled in from a saved default, which
   /// shows as the mask. What was typed into the box is the view model's token
   /// already, and stays.
   /// </summary>
   private void FollowTheToken()
   {
      if(_takingWhatWasTyped)
      {
         // The box is being typed into right now: it is not changed under the hand that types.
         return;
      }

      string token = ViewModel.Token;
      string shown = TokenBox.Password;
      if(token.Length == 0 && shown.Length > 0)
      {
         TokenBox.Password = "";
      }
      else if(token.Length > 0
          && !string.Equals(shown, token, StringComparison.Ordinal)
          && !string.Equals(shown, SettingsTokenState.Mask, StringComparison.Ordinal))
      {
         TokenBox.Password = SettingsTokenState.Mask;
      }
   }



   /// <summary>
   /// Hands the server address to the view model, which takes the place it names.
   /// Called when the box is left or Enter is pressed, and before a token is taken
   /// from the token box: a token is only ever sent to an address that was finished.
   /// </summary>
   private void CommitTheServer()
   {
      if(ViewModel is not null && ViewModel.AsksForServer && !string.Equals(ServerBox.Text, ViewModel.Server, StringComparison.Ordinal))
      {
         ViewModel.Server = ServerBox.Text;
      }
   }



   private void ServerBox_LostFocus(object sender, RoutedEventArgs e) => CommitTheServer();



   private void ServerBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
   {
      if(e.Key == Windows.System.VirtualKey.Enter)
      {
         CommitTheServer();
      }
   }



   // PasswordBox does not support reliable two-way x:Bind on Password; mirror it
   // into the view model by hand. The mask is not a token: while the box shows it,
   // the view model keeps the token it already holds.
   private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e)
   {
      string typed = ((PasswordBox)sender).Password;
      if(ViewModel is not null && ViewModel.AccountId is null && !_takingWhatWasTyped
          && !string.Equals(typed, SettingsTokenState.Mask, StringComparison.Ordinal))
      {
         ///////////////////////////////////////////////////////////////////////////
         // The address first, in case its box was not left the usual way: the    //
         // token is for the place that is on the form. Taking the address can    //
         // empty the view model's token (another place); the box keeps what is   //
         // being typed, which is then the token of the place just taken.         //
         ///////////////////////////////////////////////////////////////////////////
         _takingWhatWasTyped = true;
         try
         {
            CommitTheServer();
            ViewModel.Token = typed;
         }
         finally
         {
            _takingWhatWasTyped = false;
         }
      }
   }



   /// <summary>True while <see cref="TokenBox_PasswordChanged"/> hands what was typed to the view model.</summary>
   private bool _takingWhatWasTyped;



   // An editable ComboBox does not render Text that was set before its template
   // loaded, so an account workspace's seeded org would show as blank even though
   // the view model holds it — see EditableComboBox.
   private void OrgBox_Loaded(object sender, RoutedEventArgs e)
   {
      if(ViewModel is not null)
      {
         EditableComboBox.ReapplyText((ComboBox)sender, ViewModel.Organization);
         // The organization lookup replaces the list about a second after an
         // account workspace opens, and an editable ComboBox drops its text when
         // its ItemsSource changes; the view model restores Organization, so put
         // it back into the box each time the list changes.
         if(!ReferenceEquals(_organizationsHooked, ViewModel))
         {
            _organizationsHooked = ViewModel;
            ViewModel.Organizations.CollectionChanged += (_, _) =>
                EditableComboBox.ReapplyText((ComboBox)sender, ViewModel.Organization);
         }
      }
   }



   /// <summary>The view model whose organization list this control already follows.</summary>
   private WorkspaceViewModel? _organizationsHooked;



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
