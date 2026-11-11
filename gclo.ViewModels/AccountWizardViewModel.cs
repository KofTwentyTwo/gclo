/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using gclo.Engine;


namespace gclo.ViewModels;


/// <summary>
/// Drives the four-step add/edit account wizard: identity (name + description, and
/// for a new account the place it lives: GitHub or a Gitea server), token (validated
/// by listing the organizations it can see at that place), organization, and
/// destination. <see cref="TryAdvanceAsync"/> gates each step; <see cref="SaveAsync"/>
/// persists the result through the <see cref="AccountsStore"/>, writing the token to
/// the vault only when the user actually changed it (or always, for a new account).
/// </summary>
/// <remarks>
/// A token is sent to one place only: the one the account is on. A wizard that was
/// given one lister is for the place of that lister (the account being edited, or
/// GitHub) and offers no choice; one that was given a lister per place lets step 1
/// choose, and forgets the token when the place changes after it was typed.
/// </remarks>
public sealed partial class AccountWizardViewModel : ObservableObject
{
   private readonly AccountsStore _store;

   /// <summary>
   /// The lister of the place the account is on: the one place a token of this
   /// wizard is sent to. Replaced when step 1 chooses another place.
   /// </summary>
   private IOrganizationLister _orgLister;

   /// <summary>The lister of a place, when step 1 may choose the place; null when the place is fixed.</summary>
   private readonly Func<ForgeEndpoint, IOrganizationLister>? _listerOf;

   /// <summary>The place of a new account, as step 1 last accepted it.</summary>
   private ForgeEndpoint _chosen = ForgeEndpoint.GitHub;

   /// <summary>The place of the account being edited; null for a new account, and when what is stored cannot be used.</summary>
   private readonly ForgeEndpoint? _existingPlace;

   private readonly IActivityLog _log;

   private readonly Account? _existing;

   /// <summary>
   /// For edits: fetches the account's stored token from the vault, called only when
   /// a step actually needs to transmit it (validating on step 2 with the box left
   /// empty). The token is never copied into <see cref="Token"/>, so the dialog never
   /// holds or shows it; an empty box means "keep the stored token" (#32).
   /// </summary>
   private readonly Func<string?>? _storedToken;

   /// <summary>
   /// Fetches the default token from Settings on demand (null, or returning null,
   /// when none is saved). Read when step 2 validates or the account is saved with
   /// "Use the default token"; never copied into <see cref="Token"/> (#102).
   /// </summary>
   private readonly Func<string?>? _defaultToken;



   /// <summary>
   /// A wizard for a new account seeded from <paramref name="defaults"/>, or — when
   /// <paramref name="existing"/> is given — an edit wizard seeded from that account.
   /// <paramref name="storedToken"/> fetches the vault's current token on demand for
   /// an edit (null, or returning null, when the vault has no entry);
   /// <paramref name="defaultToken"/> fetches the default token from Settings the
   /// same way and, when it returns one, offers "Use the default token" on step 2.
   /// </summary>
   public AccountWizardViewModel(
       AccountsStore store,
       IOrganizationLister orgLister,
       AppSettings defaults,
       Account? existing = null,
       Func<string?>? storedToken = null,
       IActivityLog? log = null,
       Func<string?>? defaultToken = null)
       : this(store, orgLister, listerOf: null, existing, storedToken, defaultToken, log)
   {
      ArgumentNullException.ThrowIfNull(defaults);

      if(existing is null)
      {
         Name = "";
         Description = "";
         Organization = "";
         TargetRoot = defaults.DefaultTargetFolder;
         CreateOrgSubfolder = false;
         MaxConcurrency = defaults.DefaultMaxConcurrency;
      }
      else
      {
         Name = existing.Name;
         Description = existing.Description;
         Organization = existing.Organization;
         TargetRoot = existing.TargetRoot;
         CreateOrgSubfolder = existing.CreateOrgSubfolder;
         MaxConcurrency = existing.MaxConcurrency;
      }
   }



   /// <summary>
   /// A wizard for a NEW account whose place is chosen on step 1: GitHub, or a Gitea
   /// server and its address. <paramref name="listerOf"/> gives the organization
   /// lister of a place; the token of step 2 goes to the lister of the chosen place
   /// and to no other.
   /// </summary>
   public AccountWizardViewModel(
       AccountsStore store,
       Func<ForgeEndpoint, IOrganizationLister> listerOf,
       AppSettings defaults,
       IActivityLog? log = null,
       Func<string?>? defaultToken = null)
       : this(store, ListerOfGitHub(listerOf), listerOf, existing: null, storedToken: null, defaultToken, log)
   {
      ArgumentNullException.ThrowIfNull(defaults);
      TargetRoot = defaults.DefaultTargetFolder;
      MaxConcurrency = defaults.DefaultMaxConcurrency;
   }



   /// <summary>
   /// A wizard for a NEW account seeded from a working Quick Sync connection
   /// (<paramref name="seed"/>): token, organization, folder, subfolder preference,
   /// and parallelism are carried over so "save this connection as an account" is a
   /// matter of naming it (#30). The token is treated as freshly typed, so it is
   /// always written to the vault on save.
   /// </summary>
   public AccountWizardViewModel(
       AccountsStore store, IOrganizationLister orgLister, AccountWizardSeed seed, IActivityLog? log = null,
       Func<string?>? defaultToken = null)
       : this(store, orgLister, listerOf: null, existing: null, storedToken: null, defaultToken, log)
   {
      ArgumentNullException.ThrowIfNull(seed);
      Name = "";
      Description = "";
      Token = seed.Token;
      // A Quick Sync connection that runs on the default token becomes a
      // default-token account; any other token is the account's own. Compared in
      // memory only; neither value is logged.
      UseDefaultToken = HasDefaultToken
          && string.Equals(seed.Token.Trim(), _defaultToken?.Invoke()?.Trim(), StringComparison.Ordinal);
      Organization = seed.Organization;
      TargetRoot = seed.TargetRoot;
      CreateOrgSubfolder = seed.CreateOrgSubfolder;
      MaxConcurrency = seed.MaxConcurrency;
   }



   /// <summary>A new account starts on GitHub; the lister of that place is the one the wizard begins with.</summary>
   private static IOrganizationLister ListerOfGitHub(Func<ForgeEndpoint, IOrganizationLister> listerOf)
   {
      ArgumentNullException.ThrowIfNull(listerOf);
      return listerOf(ForgeEndpoint.GitHub);
   }



   private AccountWizardViewModel(
       AccountsStore store, IOrganizationLister orgLister, Func<ForgeEndpoint, IOrganizationLister>? listerOf,
       Account? existing, Func<string?>? storedToken, Func<string?>? defaultToken, IActivityLog? log)
   {
      ArgumentNullException.ThrowIfNull(store);
      ArgumentNullException.ThrowIfNull(orgLister);
      _store = store;
      _orgLister = orgLister;
      _listerOf = listerOf;
      _log = log ?? new NullActivityLog();
      _existing = existing;
      _existingPlace = existing is not null && existing.TryGetEndpoint(out ForgeEndpoint? place, out _) ? place : null;
      _storedToken = existing is null ? null : storedToken;
      _defaultToken = defaultToken;
      // Decided once: the choice is offered only when a default token exists at
      // open time, and the value itself is fetched again when a step needs it.
      HasDefaultToken = !string.IsNullOrWhiteSpace(defaultToken?.Invoke());
      UseDefaultToken = existing is null ? HasDefaultToken : existing.UsesDefaultToken && existing.Forge == ForgeKind.GitHub;

      Step = 1;
      NameError = "";
      Server = "";
      ServerError = "";
      TokenError = "";
      OrganizationError = "";
      TargetError = "";
      Token = "";
      Name = "";
      Description = "";
      Organization = "";
      TargetRoot = "";
   }



   /// <summary>Current wizard step, 1 (identity) through 4 (destination).</summary>
   [ObservableProperty]
   [NotifyPropertyChangedFor(nameof(IsFirstStep))]
   [NotifyPropertyChangedFor(nameof(IsLastStep))]
   public partial int Step { get; set; }

   /// <summary>Display name for the account; required and unique across accounts.</summary>
   [ObservableProperty]
   public partial string Name { get; set; }

   /// <summary>Optional free-form note about what the account is for.</summary>
   [ObservableProperty]
   public partial string Description { get; set; }

   /// <summary>
   /// The token as typed; validated when leaving step 2. For an edit it starts
   /// empty and stays empty unless the user types a replacement — the stored token
   /// is never loaded into it. Emptied when step 1 changes the place: a token is
   /// typed for one place.
   /// </summary>
   [ObservableProperty]
   public partial string Token { get; set; }

   /// <summary>
   /// Whether step 1 asks where the account lives. Only for a new account in a
   /// wizard that was given a lister per place; an account being edited stays where
   /// it is, and a Quick Sync connection saved as an account is a GitHub connection.
   /// </summary>
   public bool CanChoosePlace => _listerOf is not null;

   /// <summary>Step 1's choice: false is GitHub, true is a Gitea server whose address is <see cref="Server"/>.</summary>
   [ObservableProperty]
   [NotifyPropertyChangedFor(nameof(PlaceIndex))]
   [NotifyPropertyChangedFor(nameof(AsksForServer))]
   public partial bool IsOnGitea { get; set; }

   /// <summary>
   /// <see cref="IsOnGitea"/> as the selected index of a two-item radio group:
   /// 0 = GitHub, 1 = a Gitea server. Any other index (a radio group reports -1
   /// while it has no selection) changes nothing.
   /// </summary>
   public int PlaceIndex
   {
      get => IsOnGitea ? 1 : 0;
      set
      {
         if(value is 0 or 1)
         {
            IsOnGitea = value == 1;
         }
      }
   }

   /// <summary>Whether step 1 shows the box for the server's address.</summary>
   public bool AsksForServer => CanChoosePlace && IsOnGitea;

   /// <summary>
   /// The address of the Gitea server as typed; after step 1 accepted it, as it was
   /// normalized, which is the address the token is sent to. Empty for an edit:
   /// where the account is, <see cref="PlaceNote"/> says, and what is stored is not
   /// put into a box (an address that cannot be used may hold a password).
   /// </summary>
   [ObservableProperty]
   public partial string Server { get; set; }

   /// <summary>Step 1's message about the server address; empty when it is acceptable.</summary>
   [ObservableProperty]
   public partial string ServerError { get; set; }

   /// <summary>
   /// How the place the token goes to is named: "GitHub", or the server's host. For
   /// an account whose stored place cannot be used, what <see cref="Account.DescribePlace"/> says.
   /// </summary>
   public string PlaceName => _existing is null ? _chosen.DisplayName : _existing.DescribePlace();

   /// <summary>
   /// Said on step 1 when the place cannot be chosen: where the account is, and for
   /// an edit why that stays. Empty when step 1 asks.
   /// </summary>
   public string PlaceNote
       => CanChoosePlace ? ""
           : _existing is null ? $"This account is on {PlaceName}."
           : Place is null ? $"This account is on {PlaceName}. {_existing.AdviceWhenUnusable("Delete")}"
           : $"This account is on {PlaceName}. That cannot be changed: its token was saved for that place.";

   /// <summary>The header of step 2's token box: what the forge calls a token.</summary>
   public string TokenHeader => ForgeTexts.TokenName(PlaceKind);

   /// <summary>What step 2's empty token box says.</summary>
   public string TokenPlaceholder
       => _existing is { UsesDefaultToken: false } ? "Leave empty to keep the stored token"
           : PlaceKind == ForgeKind.GitHub ? "ghp_…"
           : "";

   /// <summary>The tooltip of step 2's token box: where the token is kept and what it needs.</summary>
   public string TokenAdvice => $"Stored in Windows Credential Manager, never in a file; {ForgeTexts.TokenNeeds(PlaceKind)}";

   /// <summary>The page a token is made on; null when the account's place is not known.</summary>
   public Uri? TokenPage => ForgeTexts.TokenPage(Place);

   /// <summary>
   /// Step 2's note on what Next does, naming the place the token is sent to: for a
   /// server its whole address as it was normalized, not the host alone, since two
   /// servers can share a host.
   /// </summary>
   public string TokenStepNote
       => $"Next checks the token with {Place?.Server?.AbsoluteUri ?? PlaceName} and lists the organizations it can see.";

   /// <summary>Organization (or user) to sync; picked from <see cref="Organizations"/> or typed.</summary>
   [ObservableProperty]
   public partial string Organization { get; set; }

   /// <summary>Folder the sync targets; see <see cref="CreateOrgSubfolder"/>.</summary>
   [ObservableProperty]
   public partial string TargetRoot { get; set; }

   /// <summary>When set, clones land under TargetRoot\Organization rather than TargetRoot.</summary>
   [ObservableProperty]
   public partial bool CreateOrgSubfolder { get; set; }

   /// <summary>Parallel clone/pull count; kept within the <see cref="AppSettings"/> range.</summary>
   [ObservableProperty]
   public partial int MaxConcurrency { get; set; }

   /// <summary>True while step 2 is validating the token against the organization lister.</summary>
   [ObservableProperty]
   public partial bool IsValidatingToken { get; set; }

   /// <summary>
   /// True when a default token was saved in Settings when the wizard opened, which
   /// is when step 2 offers the choice between it and a token of the account's own.
   /// </summary>
   public bool HasDefaultToken { get; }

   /// <summary>
   /// Step 2's choice: true means the account resolves to the default token from
   /// Settings (<see cref="TokenSource.Default"/>) and the token box is disabled;
   /// false means a token of its own. Preselected for a new account when a default
   /// token exists, and from the account's current source for an edit.
   /// </summary>
   [ObservableProperty]
   [NotifyPropertyChangedFor(nameof(IsOwnTokenSelected))]
   [NotifyPropertyChangedFor(nameof(TokenChoiceIndex))]
   [NotifyPropertyChangedFor(nameof(DefaultTokenCaption))]
   public partial bool UseDefaultToken { get; set; }

   /// <summary>The token box is enabled only for a token of the account's own.</summary>
   public bool IsOwnTokenSelected => !UseDefaultToken;

   /// <summary>
   /// Whether the choice between the default token and a token of the account's own
   /// is shown: when a default token is saved, and also for an account that uses the
   /// default token while none is saved, which otherwise could not be given another.
   /// </summary>
   /// <remarks>
   /// Never for an account that is not on GitHub: the default token is a GitHub
   /// token, and such an account has a token of its own.
   /// </remarks>
   public bool OffersTokenChoice
       => PlaceKind == ForgeKind.GitHub && (HasDefaultToken || _existing?.UsesDefaultToken == true);

   /// <summary>
   /// <see cref="UseDefaultToken"/> as the selected index of a two-item radio group:
   /// 0 = the default token, 1 = a token of this account's own.
   /// </summary>
   public int TokenChoiceIndex
   {
      get => UseDefaultToken ? 0 : 1;
      set => UseDefaultToken = value == 0;
   }

   /// <summary>
   /// What choosing the default token means for this account, with how many other
   /// accounts share it; shown under the radio button.
   /// </summary>
   public string DefaultTokenCaption
   {
      get
      {
         if(!HasDefaultToken)
         {
            return "No default token is saved right now. Save one in Settings, or give this account a token of its own.";
         }

         int others = _store.CountUsingDefaultToken() - (_existing?.UsesDefaultToken == true ? 1 : 0);
         string shared = others switch
         {
            0 => "Used by Quick Sync.",
            1 => "Used by Quick Sync and 1 other account.",
            _ => $"Used by Quick Sync and {others} other accounts.",
         };
         string switching = IsEditing && _existing?.UsesDefaultToken == false && UseDefaultToken
             ? " This account's own token will be removed from Windows Credential Manager on save."
             : "";
         return shared + " Replacing it in Settings updates this account too." + switching;
      }
   }

   /// <summary>Step 1's validation message; empty when the name is acceptable.</summary>
   [ObservableProperty]
   public partial string NameError { get; set; }

   /// <summary>Step 2's validation message; empty when the token was accepted.</summary>
   [ObservableProperty]
   public partial string TokenError { get; set; }

   /// <summary>Step 3's validation message; empty once an organization is chosen.</summary>
   [ObservableProperty]
   public partial string OrganizationError { get; set; }

   /// <summary>Step 4's validation message; empty once a target folder is chosen.</summary>
   [ObservableProperty]
   public partial string TargetError { get; set; }

   /// <summary>Organizations the validated token can see; feeds step 3's editable dropdown.</summary>
   public ObservableCollection<string> Organizations { get; } = new();



   /// <summary>True on step 1, where there is no step to go back to.</summary>
   public bool IsFirstStep => Step == 1;



   /// <summary>True on step 4, where advancing means saving instead of moving on.</summary>
   public bool IsLastStep => Step == 4;



   /// <summary>True when the wizard edits an existing account rather than creating one.</summary>
   public bool IsEditing => _existing is not null;



   /// <summary>Dialog title matching the wizard's mode.</summary>
   public string Title => IsEditing ? "Edit account" : "Add account";



   /// <summary>The kind of forge the account is on: the stored one for an edit, the chosen one for a new account.</summary>
   private ForgeKind PlaceKind => _existing?.Forge ?? _chosen.Kind;



   /// <summary>The place the account is on; null for an edit whose stored forge and server cannot be used.</summary>
   private ForgeEndpoint? Place => _existing is null ? _chosen : _existingPlace;



   partial void OnIsOnGiteaChanged(bool value) => ServerError = "";



   partial void OnServerChanged(string value) => ServerError = "";



   partial void OnMaxConcurrencyChanged(int value)
   {
      int clamped = Math.Clamp(value, AppSettings.MinConcurrency, AppSettings.MaxConcurrency);
      if(clamped != value)
      {
         MaxConcurrency = clamped;
      }
   }



   /// <summary>
   /// Validates the current step. Steps 1-3 advance and return true on success; step 4
   /// returns true without advancing (the host then calls <see cref="SaveAsync"/>). On
   /// failure the wizard stays put with the step's error message set
   /// (<see cref="NameError"/>, <see cref="TokenError"/>, <see cref="OrganizationError"/>,
   /// <see cref="TargetError"/>) — a silent no-op on Next reads as a broken button (#30).
   /// </summary>
   public async Task<bool> TryAdvanceAsync()
   {
      if(IsValidatingToken)
      {
         ///////////////////////////////////////////////////////////////////////////
         // A token is being checked with the place the wizard is on. Until the   //
         // answer is there the wizard stays as it is (GoBack too): the answer is //
         // then the answer for the place and the token the wizard still holds.   //
         ///////////////////////////////////////////////////////////////////////////
         return false;
      }

      switch(Step)
      {
         case 1:
            {
               string name = Name.Trim();
               if(name.Length == 0)
               {
                  NameError = "Enter a name for this account.";
                  return false;
               }
               Account? clash = _store.FindByName(name);
               if(clash is not null && clash.Id != _existing?.Id)
               {
                  NameError = $"An account named '{clash.Name}' already exists.";
                  return false;
               }
               NameError = "";
               if(_listerOf is not null && !TryChoosePlace(_listerOf))
               {
                  return false;
               }
               Step = 2;
               return true;
            }
         case 2:
            {
               // The lister is the validation: it fails on a rejected or rate-limited
               // token and returns the organizations the dropdown offers otherwise.
               // An edit with the box left empty validates the STORED token, fetched
               // here and used for this one call only.
               string candidate;
               if(UseDefaultToken && PlaceKind != ForgeKind.GitHub)
               {
                  ///////////////////////////////////////////////////////////////////
                  // The choice is not shown for such an account. Whatever set it, //
                  // the default token is a GitHub token and is not sent anywhere  //
                  // else.                                                         //
                  ///////////////////////////////////////////////////////////////////
                  TokenError = "The default token is a GitHub token. This account needs a token of its own.";
                  return false;
               }

               if(UseDefaultToken)
               {
                  // The default token is fetched for this one call only; the box
                  // stays untouched (and disabled) so it is never shown or retained.
                  candidate = _defaultToken?.Invoke()?.Trim() ?? "";
                  if(candidate.Length == 0)
                  {
                     TokenError = "The default token is no longer saved. Save one in Settings, "
                         + "or use a different token for this account.";
                     return false;
                  }
               }
               else
               {
                  candidate = Token.Trim();
                  if(candidate.Length == 0 && IsEditing)
                  {
                     candidate = _storedToken?.Invoke()?.Trim() ?? "";
                     if(candidate.Length == 0)
                     {
                        TokenError = _existing?.UsesDefaultToken == true
                            ? "Enter the token this account should use instead of the default one."
                            : "This account has no stored token. Enter one to continue.";
                        return false;
                     }
                  }
                  else if(candidate.Length == 0)
                  {
                     // Nothing to check: say so here, instead of asking the server
                     // and showing its argument error (#172).
                     TokenError = "Enter a token to continue.";
                     return false;
                  }
               }

               IsValidatingToken = true;
               _log.Info("Account wizard: validating the token.");
               try
               {
                  IReadOnlyList<string> organizations = await _orgLister.ListOrganizationsAsync(candidate);
                  // An editable ComboBox resets its text when its ItemsSource is
                  // mutated, and the TwoWay binding writes that empty text back here,
                  // wiping the organization an edit (or a seed) started with. Capture,
                  // restore, and re-raise so step 3 shows what the account has.
                  string organizationBeforeRefresh = Organization;
                  Organizations.Clear();
                  foreach(string organization in organizations)
                  {
                     Organizations.Add(organization);
                  }
                  Organization = organizationBeforeRefresh;
                  OnPropertyChanged(nameof(Organization));
                  TokenError = "";
                  _log.Info($"Account wizard: token accepted; {organizations.Count} organizations and accounts visible.");
               }
               catch(Exception ex)
               {
                  TokenError = ex.Message;
                  _log.Error($"Account wizard: token rejected: {ex.Message}", ex);
                  return false;
               }
               finally
               {
                  IsValidatingToken = false;
               }
               Step = 3;
               return true;
            }
         case 3:
            if(string.IsNullOrWhiteSpace(Organization))
            {
               // The dropdown allows free text, but not nothing.
               OrganizationError = "Choose an organization from the list, or type one.";
               return false;
            }
            OrganizationError = "";
            Step = 4;
            return true;
         default:
            // Step 4: valid means "ready to save"; the host closes via SaveAsync.
            if(string.IsNullOrWhiteSpace(TargetRoot))
            {
               TargetError = "Choose a target folder.";
               return false;
            }
            TargetError = "";
            return true;
      }
   }



   /// <summary>
   /// Step 1, for a new account: takes the place that was chosen, or says in
   /// <see cref="ServerError"/> why the server address cannot be used. The address
   /// is checked by the rule the command line uses (<see cref="ForgeEndpoint.TryCreate"/>),
   /// and shown afterwards as it was normalized.
   /// </summary>
   private bool TryChoosePlace(Func<ForgeEndpoint, IOrganizationLister> listerOf)
   {
      if(IsOnGitea && string.IsNullOrWhiteSpace(Server))
      {
         ServerError = "Enter the address of the server, like https://git.example.com.";
         return false;
      }

      if(!ForgeEndpoint.TryCreate(IsOnGitea ? ForgeKind.Gitea : ForgeKind.GitHub, IsOnGitea ? Server : null, out ForgeEndpoint? place, out string? reason))
      {
         ServerError = char.ToUpperInvariant(reason[0]) + reason[1..] + ".";
         return false;
      }

      ServerError = "";
      if(!place.Equals(_chosen))
      {
         //////////////////////////////////////////////////////////////////////////////
         // Another place than the one the wizard was on. What was entered for the   //
         // other place is not carried over: a token typed for one server is not     //
         // sent to another, and an organization of one is not one of the other. The //
         // default token is a GitHub token, so it is not the choice anywhere else.  //
         //////////////////////////////////////////////////////////////////////////////
         _orgLister = listerOf(place); // first: if there is no lister for the place, the wizard stays on the one it has
         _chosen = place;
         Token = "";
         TokenError = "";
         Organizations.Clear();
         Organization = "";
         UseDefaultToken = place.Kind == ForgeKind.GitHub && HasDefaultToken;
         OnPropertyChanged(nameof(PlaceName));
         OnPropertyChanged(nameof(OffersTokenChoice));
         OnPropertyChanged(nameof(TokenHeader));
         OnPropertyChanged(nameof(TokenPlaceholder));
         OnPropertyChanged(nameof(TokenAdvice));
         OnPropertyChanged(nameof(TokenPage));
         OnPropertyChanged(nameof(TokenStepNote));
      }

      if(place.Server is not null)
      {
         Server = place.Server.AbsoluteUri;
      }
      return true;
   }



   /// <summary>Returns to the previous step; a no-op on the first step, and while step 2 is checking a token.</summary>
   public void GoBack()
   {
      if(!IsFirstStep && !IsValidatingToken)
      {
         Step--;
      }
   }



   /// <summary>
   /// Persists the wizard's account: a new account gets a fresh id and always writes
   /// its token to the vault; an edit keeps the existing id and last-sync fields and
   /// touches the vault only when the token was changed. All string inputs are trimmed.
   /// </summary>
   public Task SaveAsync()
   {
      // A default-token account stores no token of its own (the store removes any
      // leftover entry when an account switches); otherwise new accounts always
      // persist their token and edits only when one was typed.
      _store.Save(BuildAccount(), !UseDefaultToken && TokenChanged ? Token.Trim() : null);
      return Task.CompletedTask;
   }



   /// <summary>New accounts always persist their token; edits only when one was typed.</summary>
   private bool TokenChanged => !IsEditing || Token.Trim().Length > 0;



   private Account BuildAccount() => new()
   {
      Id = _existing?.Id ?? Guid.NewGuid(),
      Name = Name.Trim(),
      Description = Description.Trim(),
      Organization = Organization.Trim(),
      TargetRoot = TargetRoot.Trim(),
      CreateOrgSubfolder = CreateOrgSubfolder,
      MaxConcurrency = Math.Clamp(MaxConcurrency, AppSettings.MinConcurrency, AppSettings.MaxConcurrency),
      TokenSource = UseDefaultToken ? TokenSource.Default : TokenSource.Own,

      // An account stays on the forge and server it was added for: its token was saved for that place.
      Forge = PlaceKind,
      Server = _existing is null ? _chosen.Server?.AbsoluteUri : _existing.Server,
      LastSyncUtc = _existing?.LastSyncUtc,
      LastSyncSummary = _existing?.LastSyncSummary,
   };
}
