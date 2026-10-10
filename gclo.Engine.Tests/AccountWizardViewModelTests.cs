/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.ViewModels;


namespace gclo.Engine.Tests;


/// <summary>
/// Tests for <see cref="AccountWizardViewModel"/>: step gating, token validation,
/// defaults-vs-edit seeding, and save semantics. Every wizard runs against a real
/// <see cref="AccountsStore"/> in a unique temp directory, an <see cref="InMemoryVault"/>,
/// and a <see cref="FakeOrganizationLister"/>.
/// </summary>
public sealed class AccountWizardViewModelTests : IDisposable
{
   private readonly string _root =
       Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

   private readonly InMemoryVault _vault = new();

   private readonly FakeOrganizationLister _orgs = new();

   private readonly AccountsStore _store;

   private readonly AppSettings _defaults = new()
   {
      DefaultTargetFolder = @"C:\repos-default",
      DefaultMaxConcurrency = 6,
   };



   public AccountWizardViewModelTests()
   {
      _store = new AccountsStore(_vault, _root);
   }



   public void Dispose() => GitTestHelpers.TryDeleteDirectory(_root);



   private AccountWizardViewModel NewWizard(Account? existing = null, string? existingToken = null)
       => new(_store, _orgs, _defaults, existing, () => existingToken);



   private static Account MakeAccount(string name) => new()
   {
      Id = Guid.NewGuid(),
      Name = name,
      Organization = "acme",
      TargetRoot = @"C:\repos",
   };



   /// <summary>Advances until the wizard sits on <paramref name="step"/>, asserting each hop.</summary>
   private static async Task AdvanceToStepAsync(AccountWizardViewModel wizard, int step)
   {
      while(wizard.Step < step)
      {
         int before = wizard.Step;
         Assert.True(await wizard.TryAdvanceAsync(), $"expected to advance past step {before}");
      }
      Assert.Equal(step, wizard.Step);
   }



   /// <summary>A new-account wizard walked to the last step with valid inputs everywhere.</summary>
   private async Task<AccountWizardViewModel> CreateWizardAtLastStepAsync()
   {
      AccountWizardViewModel wizard = NewWizard();
      wizard.Name = "Work";
      wizard.Token = "ghp_token";
      await AdvanceToStepAsync(wizard, 3);
      wizard.Organization = "acme";
      await AdvanceToStepAsync(wizard, 4);
      return wizard;
   }



   // ---------------------------------------------------------------- seeding

   [Fact]
   public void NewWizard_StartsAtStepOne_SeededFromDefaults()
   {
      AccountWizardViewModel wizard = NewWizard();

      Assert.Equal(1, wizard.Step);
      Assert.True(wizard.IsFirstStep);
      Assert.False(wizard.IsLastStep);
      Assert.False(wizard.IsEditing);
      Assert.Equal("Add account", wizard.Title);
      Assert.Equal("", wizard.Name);
      Assert.Equal("", wizard.Description);
      Assert.Equal("", wizard.Token);
      Assert.Equal("", wizard.Organization);
      Assert.Equal(@"C:\repos-default", wizard.TargetRoot);
      Assert.False(wizard.CreateOrgSubfolder);
      Assert.Equal(6, wizard.MaxConcurrency);
      Assert.Equal("", wizard.NameError);
      Assert.Equal("", wizard.TokenError);
   }



   [Fact]
   public void EditWizard_SeedsEveryFieldFromTheExistingAccount_ButNeverItsToken()
   {
      Account account = MakeAccount("Work") with
      {
         Description = "primary org",
         Organization = "acme-inc",
         TargetRoot = @"C:\work-repos",
         CreateOrgSubfolder = true,
         MaxConcurrency = 4,
      };

      AccountWizardViewModel wizard = NewWizard(account, "ghp_original");

      Assert.True(wizard.IsEditing);
      Assert.Equal("Edit account", wizard.Title);
      Assert.Equal("Work", wizard.Name);
      Assert.Equal("primary org", wizard.Description);
      Assert.Equal("", wizard.Token); // the stored token is never loaded into the box (#32)
      Assert.Equal("acme-inc", wizard.Organization);
      Assert.Equal(@"C:\work-repos", wizard.TargetRoot);
      Assert.True(wizard.CreateOrgSubfolder);
      Assert.Equal(4, wizard.MaxConcurrency);
   }



   [Fact]
   public async Task EditWizard_WithNoVaultToken_CannotPassStepTwoWithAnEmptyBox()
   {
      AccountWizardViewModel wizard = NewWizard(MakeAccount("Work"), existingToken: null);
      Assert.Equal("", wizard.Token);
      await AdvanceToStepAsync(wizard, 2);

      Assert.False(await wizard.TryAdvanceAsync());
      Assert.Equal("This account has no stored token. Enter one to continue.", wizard.TokenError);

      wizard.Token = "ghp_typed";
      Assert.True(await wizard.TryAdvanceAsync());
   }



   [Fact]
   public async Task EditWizard_EmptyBox_ValidatesWithTheStoredToken_WithoutExposingIt()
   {
      string? tokenSeenByLister = null;
      _orgs.Handler = (token, _) =>
      {
         tokenSeenByLister = token;
         return Task.FromResult<IReadOnlyList<string>>(["me"]);
      };
      AccountWizardViewModel wizard = NewWizard(MakeAccount("Work"), "ghp_stored");
      await AdvanceToStepAsync(wizard, 2);

      Assert.True(await wizard.TryAdvanceAsync());

      Assert.Equal("ghp_stored", tokenSeenByLister); // fetched for the call...
      Assert.Equal("", wizard.Token); // ...and still not in the box
   }



   [Fact]
   public void MaxConcurrency_IsClampedToTheSettingsRange()
   {
      AccountWizardViewModel wizard = NewWizard();

      wizard.MaxConcurrency = 0;
      Assert.Equal(AppSettings.MinConcurrency, wizard.MaxConcurrency);

      wizard.MaxConcurrency = 1000;
      Assert.Equal(AppSettings.MaxConcurrency, wizard.MaxConcurrency);
   }



   // ---------------------------------------------------------------- step 1: identity

   [Fact]
   public async Task TryAdvance_BlankName_SetsNameErrorAndStays()
   {
      AccountWizardViewModel wizard = NewWizard();
      wizard.Name = "   ";

      Assert.False(await wizard.TryAdvanceAsync());

      Assert.Equal(1, wizard.Step);
      Assert.NotEqual("", wizard.NameError, StringComparer.Ordinal);
   }



   [Fact]
   public async Task TryAdvance_DuplicateName_SetsNameErrorAndStays_ThenClearsOnceFixed()
   {
      _store.Save(MakeAccount("Work"), null);
      AccountWizardViewModel wizard = NewWizard();
      wizard.Name = "WORK"; // uniqueness is case-insensitive

      Assert.False(await wizard.TryAdvanceAsync());
      Assert.Equal(1, wizard.Step);
      Assert.Contains("Work", wizard.NameError, StringComparison.Ordinal);

      wizard.Name = "Personal";
      Assert.True(await wizard.TryAdvanceAsync());
      Assert.Equal(2, wizard.Step);
      Assert.Equal("", wizard.NameError);
   }



   [Fact]
   public async Task TryAdvance_EditKeepingItsOwnName_Advances()
   {
      Account account = MakeAccount("Work");
      _store.Save(account, null);
      AccountWizardViewModel wizard = NewWizard(account, "ghp_original");

      Assert.True(await wizard.TryAdvanceAsync());
      Assert.Equal(2, wizard.Step);
   }



   [Fact]
   public async Task TryAdvance_EditTakingAnotherAccountsName_IsBlocked()
   {
      Account account = MakeAccount("Work");
      _store.Save(account, null);
      _store.Save(MakeAccount("Personal"), null);
      AccountWizardViewModel wizard = NewWizard(account, "ghp_original");
      wizard.Name = "personal";

      Assert.False(await wizard.TryAdvanceAsync());
      Assert.Equal(1, wizard.Step);
      Assert.NotEqual("", wizard.NameError, StringComparer.Ordinal);
   }



   // ---------------------------------------------------------------- step 2: token

   [Fact]
   public async Task TryAdvance_AcceptedToken_FillsOrganizations_ClearsError_Advances()
   {
      string? validatedToken = null;
      _orgs.Handler = (token, _) =>
      {
         validatedToken = token;
         return Task.FromResult<IReadOnlyList<string>>(new[] { "me", "acme" });
      };
      AccountWizardViewModel wizard = NewWizard();
      wizard.Name = "Work";
      await AdvanceToStepAsync(wizard, 2);
      wizard.Token = "  ghp_valid  ";

      Assert.True(await wizard.TryAdvanceAsync());

      Assert.Equal(3, wizard.Step);
      Assert.Equal("ghp_valid", validatedToken); // validated as it will be saved: trimmed
      Assert.Equal(new[] { "me", "acme" }, wizard.Organizations, StringComparer.Ordinal);
      Assert.Equal("", wizard.TokenError);
      Assert.False(wizard.IsValidatingToken);
   }



   [Fact]
   public async Task TryAdvance_RejectedToken_SetsTokenErrorAndStays()
   {
      _orgs.Handler = (_, _) => throw new InvalidOperationException("GitHub rejected the token (401).");
      AccountWizardViewModel wizard = NewWizard();
      wizard.Name = "Work";
      await AdvanceToStepAsync(wizard, 2);
      wizard.Token = "ghp_bad";

      Assert.False(await wizard.TryAdvanceAsync());

      Assert.Equal(2, wizard.Step);
      Assert.Equal("GitHub rejected the token (401).", wizard.TokenError);
      Assert.False(wizard.IsValidatingToken);
   }



   [Fact]
   public async Task IsValidatingToken_IsTrueExactlyWhileTheLookupIsInFlight()
   {
      var gate = new TaskCompletionSource<IReadOnlyList<string>>(
          TaskCreationOptions.RunContinuationsAsynchronously);
      _orgs.Handler = (_, _) => gate.Task;
      AccountWizardViewModel wizard = NewWizard();
      wizard.Name = "Work";
      await AdvanceToStepAsync(wizard, 2);
      wizard.Token = "ghp_slow";

      Task<bool> advance = wizard.TryAdvanceAsync();
      Assert.True(wizard.IsValidatingToken);

      gate.SetResult(new[] { "me" });
      Assert.True(await advance);
      Assert.False(wizard.IsValidatingToken);
      Assert.Equal(3, wizard.Step);
   }



   // ---------------------------------------------------------------- steps 3 + 4

   [Fact]
   public async Task TryAdvance_BlankOrganization_StaysOnStepThree()
   {
      AccountWizardViewModel wizard = NewWizard();
      wizard.Name = "Work";
      wizard.Token = "ghp_token";
      await AdvanceToStepAsync(wizard, 3);

      Assert.False(await wizard.TryAdvanceAsync());
      Assert.Equal(3, wizard.Step);
      Assert.Equal("Choose an organization from the list, or type one.", wizard.OrganizationError);

      wizard.Organization = "acme";
      Assert.True(await wizard.TryAdvanceAsync());
      Assert.Equal(4, wizard.Step);
      Assert.True(wizard.IsLastStep);
      Assert.Equal("", wizard.OrganizationError);
   }



   [Fact]
   public async Task TryAdvance_BlankTargetRoot_ReturnsFalseOnStepFour()
   {
      AccountWizardViewModel wizard = await CreateWizardAtLastStepAsync();
      wizard.TargetRoot = "   ";

      Assert.False(await wizard.TryAdvanceAsync());
      Assert.Equal(4, wizard.Step);
      Assert.Equal("Choose a target folder.", wizard.TargetError);

      wizard.TargetRoot = @"C:\repos";
      Assert.True(await wizard.TryAdvanceAsync());
      Assert.Equal("", wizard.TargetError);
   }



   [Fact]
   public async Task SeededWizard_CarriesTheQuickSyncConnectionOver_AndSavesItsToken()
   {
      var seed = new AccountWizardSeed("ghp_quick", "acme", @"C:\src", CreateOrgSubfolder: true, MaxConcurrency: 12);
      var wizard = new AccountWizardViewModel(_store, _orgs, seed);

      Assert.Equal(1, wizard.Step);
      Assert.False(wizard.IsEditing);
      Assert.Equal("", wizard.Name);
      Assert.Equal("ghp_quick", wizard.Token);
      Assert.Equal("acme", wizard.Organization);
      Assert.Equal(@"C:\src", wizard.TargetRoot);
      Assert.True(wizard.CreateOrgSubfolder);
      Assert.Equal(12, wizard.MaxConcurrency);
      Assert.DoesNotContain("ghp_quick", seed.ToString());
      Assert.Contains("[redacted]", seed.ToString());

      wizard.Name = "Saved from Quick Sync";
      await AdvanceToStepAsync(wizard, 4);
      Assert.True(await wizard.TryAdvanceAsync());
      await wizard.SaveAsync();

      Account saved = Assert.Single(_store.GetAll());
      Assert.Equal("acme", saved.Organization);
      Assert.Equal(12, saved.MaxConcurrency);
      Assert.Equal("ghp_quick", _vault.TryRetrieve(saved.Id));
   }



   // ---------------------------------------------------------------- default token (#102)

   private AccountWizardViewModel NewWizardWithDefault(string? defaultToken, Account? existing = null, string? existingToken = null)
       => new(_store, _orgs, _defaults, existing, () => existingToken, defaultToken: () => defaultToken);



   [Fact]
   public void NewWizard_WithoutADefaultToken_OffersNoChoice_AndUsesAnOwnToken()
   {
      AccountWizardViewModel wizard = NewWizardWithDefault(defaultToken: null);

      Assert.False(wizard.HasDefaultToken);
      Assert.False(wizard.UseDefaultToken);
      Assert.True(wizard.IsOwnTokenSelected);
      Assert.Equal(1, wizard.TokenChoiceIndex);
   }



   [Fact]
   public void NewWizard_WithADefaultToken_PreselectsIt()
   {
      AccountWizardViewModel wizard = NewWizardWithDefault("ghp_default");

      Assert.True(wizard.HasDefaultToken);
      Assert.True(wizard.UseDefaultToken);
      Assert.False(wizard.IsOwnTokenSelected);
      Assert.Equal(0, wizard.TokenChoiceIndex);
      Assert.Equal("", wizard.Token); // the default token is never loaded into the box
      Assert.StartsWith("Used by Quick Sync.", wizard.DefaultTokenCaption, StringComparison.Ordinal);
   }



   [Fact]
   public void TokenChoiceIndex_RoundTripsToUseDefaultToken_AndNotifies()
   {
      AccountWizardViewModel wizard = NewWizardWithDefault("ghp_default");
      var changed = new List<string?>();
      wizard.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

      wizard.TokenChoiceIndex = 1;

      Assert.False(wizard.UseDefaultToken);
      Assert.Contains(changed, n => string.Equals(n, nameof(AccountWizardViewModel.IsOwnTokenSelected), StringComparison.Ordinal));
      Assert.Contains(changed, n => string.Equals(n, nameof(AccountWizardViewModel.TokenChoiceIndex), StringComparison.Ordinal));
      Assert.Contains(changed, n => string.Equals(n, nameof(AccountWizardViewModel.DefaultTokenCaption), StringComparison.Ordinal));

      wizard.TokenChoiceIndex = 0;
      Assert.True(wizard.UseDefaultToken);
   }



   [Fact]
   public async Task NewWizard_UsingTheDefaultToken_ValidatesWithIt_AndSavesWithoutAnOwnEntry()
   {
      AccountWizardViewModel wizard = NewWizardWithDefault("ghp_default");
      wizard.Name = "Shared";
      string? validated = null;
      _orgs.Handler = (token, _) =>
      {
         validated = token;
         return Task.FromResult<IReadOnlyList<string>>(["acme"]);
      };

      await AdvanceToStepAsync(wizard, 3);
      Assert.Equal("ghp_default", validated);
      Assert.Equal("", wizard.TokenError);
      wizard.Organization = "acme";
      wizard.TargetRoot = @"C:\repos";
      await AdvanceToStepAsync(wizard, 4);
      await wizard.SaveAsync();

      Account saved = Assert.Single(_store.GetAll());
      Assert.True(saved.UsesDefaultToken);
      Assert.Null(_vault.TryRetrieve(saved.Id));
      Assert.Equal(1, _store.CountUsingDefaultToken());
   }



   [Fact]
   public async Task NewWizard_UsingTheDefaultToken_WhenItWasRemovedMeanwhile_StaysOnStepTwo()
   {
      string? liveDefault = "ghp_default";
      var wizard = new AccountWizardViewModel(_store, _orgs, _defaults, defaultToken: () => liveDefault);
      wizard.Name = "Shared";
      await AdvanceToStepAsync(wizard, 2);
      liveDefault = null; // removed in Settings while the wizard is open

      Assert.False(await wizard.TryAdvanceAsync());

      Assert.Equal(2, wizard.Step);
      Assert.Contains("default token is no longer saved", wizard.TokenError);
      Assert.Contains("Settings", wizard.TokenError);
   }



   [Fact]
   public async Task NewWizard_ChoosingAnOwnToken_IgnoresTheDefault_AndStoresTheTypedOne()
   {
      AccountWizardViewModel wizard = NewWizardWithDefault("ghp_default");
      wizard.Name = "Mine";
      wizard.UseDefaultToken = false;
      wizard.Token = "ghp_mine";
      string? validated = null;
      _orgs.Handler = (token, _) =>
      {
         validated = token;
         return Task.FromResult<IReadOnlyList<string>>(["acme"]);
      };

      await AdvanceToStepAsync(wizard, 3);
      wizard.Organization = "acme";
      wizard.TargetRoot = @"C:\repos";
      await AdvanceToStepAsync(wizard, 4);
      await wizard.SaveAsync();

      Assert.Equal("ghp_mine", validated);
      Account saved = Assert.Single(_store.GetAll());
      Assert.False(saved.UsesDefaultToken);
      Assert.Equal("ghp_mine", _vault.TryRetrieve(saved.Id));
   }



   [Fact]
   public async Task EditWizard_DefaultTokenAccount_StartsOnTheDefault_AndCanSwitchToAnOwnToken()
   {
      Account existing = MakeAccount("Shared") with { TokenSource = TokenSource.Default };
      _store.Save(existing, null);
      AccountWizardViewModel wizard = NewWizardWithDefault("ghp_default", existing);
      Assert.True(wizard.UseDefaultToken);
      Assert.StartsWith("Used by Quick Sync.", wizard.DefaultTokenCaption, StringComparison.Ordinal); // itself excluded

      wizard.UseDefaultToken = false;
      await AdvanceToStepAsync(wizard, 2);
      Assert.False(await wizard.TryAdvanceAsync()); // no own token typed yet
      Assert.Contains("instead of the default one", wizard.TokenError);

      wizard.Token = "ghp_mine";
      await AdvanceToStepAsync(wizard, 4);
      await wizard.SaveAsync();

      Account saved = Assert.Single(_store.GetAll());
      Assert.False(saved.UsesDefaultToken);
      Assert.Equal("ghp_mine", _vault.TryRetrieve(saved.Id));
   }



   [Fact]
   public async Task EditWizard_OwnTokenAccount_SwitchingToTheDefault_RemovesItsOwnEntry_AndSaysSo()
   {
      Account existing = MakeAccount("Work");
      _store.Save(existing, "ghp_own");
      _store.Save(MakeAccount("Other") with { TokenSource = TokenSource.Default }, null);
      AccountWizardViewModel wizard = NewWizardWithDefault("ghp_default", existing, "ghp_own");
      Assert.False(wizard.UseDefaultToken);

      wizard.UseDefaultToken = true;

      Assert.Contains("1 other account", wizard.DefaultTokenCaption);
      Assert.Contains("own token will be removed", wizard.DefaultTokenCaption);
      await AdvanceToStepAsync(wizard, 4);
      await wizard.SaveAsync();
      Account saved = _store.FindByName("Work")!;
      Assert.True(saved.UsesDefaultToken);
      Assert.Null(_vault.TryRetrieve(saved.Id));
   }



   [Fact]
   public void DefaultTokenCaption_CountsOtherAccounts()
   {
      _store.Save(MakeAccount("A") with { TokenSource = TokenSource.Default }, null);
      _store.Save(MakeAccount("B") with { TokenSource = TokenSource.Default }, null);

      Assert.Contains("2 other accounts", NewWizardWithDefault("ghp_default").DefaultTokenCaption);
   }



   [Fact]
   public void SeededWizard_PreselectsTheDefault_OnlyWhenTheSeedTokenIsTheDefaultToken()
   {
      var onDefault = new AccountWizardSeed("ghp_default", "acme", @"C:\src", CreateOrgSubfolder: false, MaxConcurrency: 8);
      var other = new AccountWizardSeed("ghp_other", "acme", @"C:\src", CreateOrgSubfolder: false, MaxConcurrency: 8);

      Assert.True(new AccountWizardViewModel(_store, _orgs, onDefault, defaultToken: () => "ghp_default").UseDefaultToken);
      Assert.False(new AccountWizardViewModel(_store, _orgs, other, defaultToken: () => "ghp_default").UseDefaultToken);
      Assert.False(new AccountWizardViewModel(_store, _orgs, onDefault).UseDefaultToken);
   }



   [Fact]
   public void SeededWizard_NullSeed_Throws()
   {
      Assert.Throws<ArgumentNullException>(() => new AccountWizardViewModel(_store, _orgs, (AccountWizardSeed)null!));
   }



   [Fact]
   public async Task TryAdvance_ValidStepFour_ReturnsTrueWithoutAdvancing()
   {
      AccountWizardViewModel wizard = await CreateWizardAtLastStepAsync();

      Assert.True(await wizard.TryAdvanceAsync());
      Assert.Equal(4, wizard.Step); // the host reacts by calling SaveAsync
   }



   [Fact]
   public async Task GoBack_StepsBackwards_AndIsANoOpOnStepOne()
   {
      AccountWizardViewModel wizard = NewWizard();
      wizard.Name = "Work";
      wizard.Token = "ghp_token";
      await AdvanceToStepAsync(wizard, 2);

      wizard.GoBack();
      Assert.Equal(1, wizard.Step);
      Assert.True(wizard.IsFirstStep);

      wizard.GoBack();
      Assert.Equal(1, wizard.Step);
   }



   // ---------------------------------------------------------------- save: new accounts

   [Fact]
   public async Task SaveAsync_NewAccount_PersistsTheAccountAndPutsTheTokenInTheVault()
   {
      AccountWizardViewModel wizard = await CreateWizardAtLastStepAsync();
      wizard.Description = "primary org";
      wizard.CreateOrgSubfolder = true;
      wizard.MaxConcurrency = 4;
      wizard.TargetRoot = @"C:\work-repos";

      await wizard.SaveAsync();

      Account saved = Assert.Single(_store.GetAll());
      Assert.Equal("Work", saved.Name);
      Assert.Equal("primary org", saved.Description);
      Assert.Equal("acme", saved.Organization);
      Assert.Equal(@"C:\work-repos", saved.TargetRoot);
      Assert.True(saved.CreateOrgSubfolder);
      Assert.Equal(4, saved.MaxConcurrency);
      Assert.Null(saved.LastSyncUtc);
      Assert.Null(saved.LastSyncSummary);
      Assert.Equal("ghp_token", _vault.TryRetrieve(saved.Id));
   }



   [Fact]
   public async Task SaveAsync_TrimsEveryStringInput()
   {
      _orgs.Handler = (_, _) => Task.FromResult<IReadOnlyList<string>>(new[] { "acme" });
      AccountWizardViewModel wizard = NewWizard();
      wizard.Name = "  Work  ";
      wizard.Description = "  primary org  ";
      wizard.Token = "  ghp_token  ";
      await AdvanceToStepAsync(wizard, 3);
      wizard.Organization = "  acme  ";
      await AdvanceToStepAsync(wizard, 4);
      wizard.TargetRoot = @"  C:\work-repos  ";

      await wizard.SaveAsync();

      Account saved = Assert.Single(_store.GetAll());
      Assert.Equal("Work", saved.Name);
      Assert.Equal("primary org", saved.Description);
      Assert.Equal("acme", saved.Organization);
      Assert.Equal(@"C:\work-repos", saved.TargetRoot);
      Assert.Equal("ghp_token", _vault.TryRetrieve(saved.Id));
   }



   // ---------------------------------------------------------------- save: edits

   [Fact]
   public async Task SaveAsync_Edit_UpdatesFields_PreservesIdAndLastSync_LeavesVaultAlone()
   {
      Account account = MakeAccount("Work") with
      {
         LastSyncUtc = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero),
         LastSyncSummary = "Finished: 3 cloned.",
      };
      _store.Save(account, "ghp_original");
      AccountWizardViewModel wizard = NewWizard(account, "ghp_original");
      wizard.Description = "edited";
      wizard.Organization = "acme-2";
      await AdvanceToStepAsync(wizard, 4);
      // Overwrite the vault entry out of band: if SaveAsync wrote the (unchanged)
      // token back, this sentinel would be clobbered with 'ghp_original'.
      _vault.Store(account.Id, "sentinel");

      await wizard.SaveAsync();

      Account saved = Assert.Single(_store.GetAll());
      Assert.Equal(account.Id, saved.Id);
      Assert.Equal("edited", saved.Description);
      Assert.Equal("acme-2", saved.Organization);
      Assert.Equal(account.LastSyncUtc, saved.LastSyncUtc);
      Assert.Equal(account.LastSyncSummary, saved.LastSyncSummary);
      Assert.Equal("sentinel", _vault.TryRetrieve(account.Id));
   }



   [Fact]
   public async Task SaveAsync_Edit_WithAChangedToken_UpdatesTheVault()
   {
      Account account = MakeAccount("Work");
      _store.Save(account, "ghp_original");
      AccountWizardViewModel wizard = NewWizard(account, "ghp_original");
      wizard.Token = "ghp_rotated";
      await AdvanceToStepAsync(wizard, 4);

      await wizard.SaveAsync();

      Assert.Equal(account.Id, Assert.Single(_store.GetAll()).Id);
      Assert.Equal("ghp_rotated", _vault.TryRetrieve(account.Id));
   }
}
