/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;
using gclo.ViewModels;


namespace gclo.Engine.Tests;


/// <summary>
/// Where a new account lives, as the wizard's first step asks it (#206): GitHub or a
/// Gitea server and its address. The rule these tests are built around is that a
/// token goes to one place only, the one that was chosen, and that what was entered
/// for one place is not carried to another. Each lister here records the tokens it
/// was given under the place it is the lister of.
/// </summary>
public sealed class AccountWizardPlaceTests : IDisposable
{
   private const string Server = "https://git.example.test/";

   private readonly string _root =
       Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

   private readonly InMemoryVault _vault = new();

   private readonly AccountsStore _store;

   private readonly AppSettings _defaults = new()
   {
      DefaultTargetFolder = @"C:\repos-default",
      DefaultMaxConcurrency = 6,
   };

   /// <summary>Every token a lister was given, with the name of the place the lister is for.</summary>
   private readonly ConcurrentQueue<(string Place, string Token)> _sent = new();



   public AccountWizardPlaceTests()
   {
      _store = new AccountsStore(_vault, _root);
   }



   public void Dispose() => GitTestHelpers.TryDeleteDirectory(_root);



   /// <summary>The lister of a place: it records what it is given and lists one organization named after the place.</summary>
   private FakeOrganizationLister ListerOf(ForgeEndpoint place) => new()
   {
      Handler = (token, _) =>
      {
         _sent.Enqueue((place.DisplayName, token));
         return Task.FromResult<IReadOnlyList<string>>([$"org-at-{place.DisplayName}"]);
      },
   };



   private AccountWizardViewModel ChoosingWizard(string? defaultToken = null)
       => new(_store, ListerOf, _defaults, defaultToken: () => defaultToken);



   private static Account GiteaAccount(string? server = Server) => new()
   {
      Id = Guid.NewGuid(),
      Name = "Forge",
      Organization = "platform",
      TargetRoot = @"C:\repos",
      Forge = ForgeKind.Gitea,
      Server = server,
   };



   private static async Task AdvanceToStepAsync(AccountWizardViewModel wizard, int step)
   {
      while(wizard.Step < step)
      {
         int before = wizard.Step;
         Assert.True(await wizard.TryAdvanceAsync(), $"expected to advance past step {before}");
      }
      Assert.Equal(step, wizard.Step);
   }



   // ---------------------------------------------------------------- the choice

   [Fact]
   public void AWizardThatMayChoose_StartsOnGitHub_AndAsksForNoServer()
   {
      AccountWizardViewModel wizard = ChoosingWizard();

      Assert.True(wizard.CanChoosePlace);
      Assert.False(wizard.IsOnGitea);
      Assert.Equal(0, wizard.PlaceIndex);
      Assert.False(wizard.AsksForServer);
      Assert.Equal("", wizard.Server);
      Assert.Equal("", wizard.ServerError);
      Assert.Equal("", wizard.PlaceNote); // step 1 asks, so there is nothing to tell
      Assert.Equal("GitHub", wizard.PlaceName);
      Assert.Equal("Personal access token", wizard.TokenHeader);
      Assert.Equal("ghp_…", wizard.TokenPlaceholder);
      Assert.Equal("Stored in Windows Credential Manager, never in a file; needs repository read access", wizard.TokenAdvice);
      Assert.Equal(new Uri("https://github.com/settings/tokens"), wizard.TokenPage);
      Assert.Equal("Next checks the token with GitHub and lists the organizations it can see.", wizard.TokenStepNote);

      // Seeded from the defaults like any new account.
      Assert.Equal(@"C:\repos-default", wizard.TargetRoot);
      Assert.Equal(6, wizard.MaxConcurrency);
      Assert.False(wizard.IsEditing);
   }



   [Fact]
   public void PlaceIndex_IsTheChoiceAsARadioGroupReadsIt_AndNoSelectionChangesNothing()
   {
      AccountWizardViewModel wizard = ChoosingWizard();
      var changed = new List<string?>();
      wizard.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

      wizard.PlaceIndex = 1;

      Assert.True(wizard.IsOnGitea);
      Assert.True(wizard.AsksForServer);
      Assert.Equal(1, wizard.PlaceIndex);
      Assert.Contains(nameof(AccountWizardViewModel.PlaceIndex), changed, StringComparer.Ordinal);
      Assert.Contains(nameof(AccountWizardViewModel.AsksForServer), changed, StringComparer.Ordinal);

      wizard.PlaceIndex = -1; // a radio group that has no selection for a moment

      Assert.True(wizard.IsOnGitea);

      wizard.PlaceIndex = 0;

      Assert.False(wizard.IsOnGitea);
      Assert.False(wizard.AsksForServer);
   }



   // ---------------------------------------------------------------- where the token goes

   [Fact]
   public async Task AnAccountOnAGiteaServer_HasItsTokenCheckedThere_AndIsSavedThere()
   {
      AccountWizardViewModel wizard = ChoosingWizard();
      wizard.Name = "Forge";
      wizard.IsOnGitea = true;
      wizard.Server = "  git.example.test  "; // as a person types it

      await AdvanceToStepAsync(wizard, 2);

      // The address is shown as it was normalized: it is the one the token goes to.
      Assert.Equal(Server, wizard.Server);
      Assert.Equal("", wizard.ServerError);
      Assert.Equal("git.example.test", wizard.PlaceName);
      Assert.Equal("Access token", wizard.TokenHeader);
      Assert.Equal("", wizard.TokenPlaceholder);
      Assert.Equal(
          "Stored in Windows Credential Manager, never in a file; needs read access to repositories, organizations and the user",
          wizard.TokenAdvice);
      Assert.Equal(new Uri("https://git.example.test/user/settings/applications"), wizard.TokenPage);
      // The whole address, not the host alone: it is what the token is about to be sent to.
      Assert.Equal("Next checks the token with https://git.example.test/ and lists the organizations it can see.", wizard.TokenStepNote);
      Assert.Empty(_sent); // nothing was sent anywhere to get here

      wizard.Token = "gitea_token";
      await AdvanceToStepAsync(wizard, 3);

      Assert.Equal([("git.example.test", "gitea_token")], _sent);
      Assert.Equal(["org-at-git.example.test"], wizard.Organizations);

      wizard.Organization = "platform";
      await AdvanceToStepAsync(wizard, 4);
      await wizard.SaveAsync();

      Account saved = Assert.Single(new AccountsStore(_vault, _root).GetAll());
      Assert.Equal(ForgeKind.Gitea, saved.Forge);
      Assert.Equal(Server, saved.Server);
      Assert.Equal(TokenSource.Own, saved.TokenSource);
      Assert.Equal("gitea_token", _vault.TryRetrieve(saved.TokenEntryId));
      Assert.Null(_vault.TryRetrieve(saved.Id)); // not where a version that knows only GitHub looks
   }



   [Fact]
   public async Task AnAccountLeftOnGitHub_IsAGitHubAccount_AsBefore()
   {
      AccountWizardViewModel wizard = ChoosingWizard();
      wizard.Name = "Work";
      wizard.Server = "git.example.test"; // typed, then GitHub chosen after all: not used
      wizard.Token = "ghp_token";
      await AdvanceToStepAsync(wizard, 3);
      wizard.Organization = "acme";
      await AdvanceToStepAsync(wizard, 4);
      await wizard.SaveAsync();

      Assert.Equal([("GitHub", "ghp_token")], _sent);
      Account saved = Assert.Single(new AccountsStore(_vault, _root).GetAll());
      Assert.Equal(ForgeKind.GitHub, saved.Forge);
      Assert.Null(saved.Server);
      Assert.Equal("ghp_token", _vault.TryRetrieve(saved.Id));
   }



   [Theory]
   [InlineData("", "Enter the address of the server, like https://git.example.com.")]
   [InlineData("   ", "Enter the address of the server, like https://git.example.com.")]
   [InlineData("http://git.example.test", "The server address cannot be used: it has to begin with https://.")]
   [InlineData(
       "https://someone:s3cret-token@git.example.test",
       "The server address cannot be used: it contains a user name or a password; the token is entered separately.")]
   [InlineData("https:/git.example.test", "The server address cannot be used: it is not an address (a server's address looks like https://git.example.com).")]
   public async Task AServerAddressThatCannotBeUsed_KeepsTheWizardOnStepOne_AndNothingIsSent(string typed, string said)
   {
      AccountWizardViewModel wizard = ChoosingWizard();
      wizard.Name = "Forge";
      wizard.IsOnGitea = true;
      wizard.Server = typed;
      wizard.Token = "gitea_token";

      Assert.False(await wizard.TryAdvanceAsync());

      Assert.Equal(1, wizard.Step);
      Assert.Equal(said, wizard.ServerError);
      Assert.DoesNotContain("s3cret", wizard.ServerError, StringComparison.Ordinal); // a refused address is not repeated
      Assert.Equal("", wizard.NameError);
      Assert.Equal(typed, wizard.Server); // left as it was typed, to be corrected
      Assert.Equal("GitHub", wizard.PlaceName); // no place was taken
      Assert.Empty(_sent);

      // Corrected, the wizard goes on and the message goes away.
      wizard.Server = "git.example.test";

      Assert.True(await wizard.TryAdvanceAsync());
      Assert.Equal(2, wizard.Step);
      Assert.Equal("", wizard.ServerError);
   }



   [Fact]
   public async Task TheMessageAboutTheServer_GoesAway_WhenTheOtherPlaceIsChosen()
   {
      AccountWizardViewModel wizard = ChoosingWizard();
      wizard.Name = "Work";
      wizard.IsOnGitea = true;
      Assert.False(await wizard.TryAdvanceAsync());
      Assert.Equal("Enter the address of the server, like https://git.example.com.", wizard.ServerError);

      wizard.IsOnGitea = false;

      Assert.Equal("", wizard.ServerError);
      Assert.True(await wizard.TryAdvanceAsync());
   }



   [Fact]
   public async Task ABlankName_IsSaidFirst_AndTheServerIsNotLookedAt()
   {
      AccountWizardViewModel wizard = ChoosingWizard();
      wizard.IsOnGitea = true;
      wizard.Server = "http://git.example.test";

      Assert.False(await wizard.TryAdvanceAsync());

      Assert.Equal("Enter a name for this account.", wizard.NameError);
      Assert.Equal("", wizard.ServerError);
   }



   // ---------------------------------------------------------------- nothing is carried to another place

   [Fact]
   public async Task ATokenTypedForOnePlace_IsNotKept_WhenAnotherPlaceIsChosen()
   {
      AccountWizardViewModel wizard = ChoosingWizard();
      wizard.Name = "Work";
      wizard.Token = "ghp_typed_for_github";
      await AdvanceToStepAsync(wizard, 3);
      wizard.Organization = "org-at-GitHub";
      var changed = new List<string?>();
      wizard.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

      // Back to step 1, and the account is to be on a Gitea server after all.
      wizard.GoBack();
      wizard.GoBack();
      wizard.IsOnGitea = true;
      wizard.Server = Server;
      await AdvanceToStepAsync(wizard, 2);

      Assert.Equal("", wizard.Token);
      Assert.Contains(nameof(AccountWizardViewModel.Token), changed, StringComparer.Ordinal); // the dialog empties its box on this
      Assert.Equal("", wizard.Organization); // an organization of the other place
      Assert.Empty(wizard.Organizations);

      // With nothing typed for the new place, nothing is sent to it.
      Assert.False(await wizard.TryAdvanceAsync());
      Assert.Equal("Enter a token to continue.", wizard.TokenError);
      Assert.Equal([("GitHub", "ghp_typed_for_github")], _sent);

      // The same holds from one server to another.
      wizard.Token = "token_for_the_first_server";
      await AdvanceToStepAsync(wizard, 3);
      wizard.GoBack();
      wizard.GoBack();
      wizard.Server = "https://other.example.test";
      await AdvanceToStepAsync(wizard, 2);

      Assert.Equal("", wizard.Token);
      Assert.Equal("other.example.test", wizard.PlaceName);
      Assert.False(await wizard.TryAdvanceAsync());
      Assert.Equal(
          [("GitHub", "ghp_typed_for_github"), ("git.example.test", "token_for_the_first_server")],
          _sent);
   }



   [Fact]
   public async Task GoingBackWithoutChoosingAnotherPlace_KeepsWhatWasEntered()
   {
      AccountWizardViewModel wizard = ChoosingWizard();
      wizard.Name = "Forge";
      wizard.IsOnGitea = true;
      wizard.Server = "git.example.test";
      await AdvanceToStepAsync(wizard, 2);
      wizard.Token = "gitea_token";
      await AdvanceToStepAsync(wizard, 3);
      wizard.Organization = "platform";

      wizard.GoBack();
      wizard.GoBack();
      wizard.Name = "Forge, renamed";
      wizard.Server = "https://GIT.example.test"; // the same server, spelled another way
      await AdvanceToStepAsync(wizard, 2);

      Assert.Equal("gitea_token", wizard.Token);
      Assert.Equal("platform", wizard.Organization);
      Assert.Equal(Server, wizard.Server);
   }



   [Fact]
   public async Task TwoServersOnOneHost_AreToldApart_WhereTheTokenIsAskedFor()
   {
      AccountWizardViewModel wizard = ChoosingWizard();
      wizard.Name = "Forge";
      wizard.IsOnGitea = true;
      wizard.Server = "git.example.test/team-a";

      await AdvanceToStepAsync(wizard, 2);

      Assert.Equal("Next checks the token with https://git.example.test/team-a/ and lists the organizations it can see.", wizard.TokenStepNote);
      Assert.Equal(new Uri("https://git.example.test/team-a/user/settings/applications"), wizard.TokenPage);
   }



   [Fact]
   public async Task WhatStepTwoSays_FollowsThePlace_WhenItIsChosenAgain()
   {
      AccountWizardViewModel wizard = ChoosingWizard();
      wizard.Name = "Forge";
      wizard.Token = "";
      await AdvanceToStepAsync(wizard, 2);
      Assert.False(await wizard.TryAdvanceAsync());
      Assert.Equal("Enter a token to continue.", wizard.TokenError); // said about the other place
      var changed = new List<string?>();
      wizard.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

      wizard.GoBack();
      wizard.IsOnGitea = true;
      wizard.Server = Server;
      await AdvanceToStepAsync(wizard, 2);

      // Step 2 is bound to these: without the notifications it would go on naming GitHub and linking GitHub's page.
      Assert.All(
          (string[])
          [
              nameof(AccountWizardViewModel.PlaceName), nameof(AccountWizardViewModel.TokenStepNote), nameof(AccountWizardViewModel.TokenPage),
              nameof(AccountWizardViewModel.TokenHeader), nameof(AccountWizardViewModel.TokenPlaceholder), nameof(AccountWizardViewModel.TokenAdvice),
          ],
          name => Assert.Contains(name, changed, StringComparer.Ordinal));
      Assert.Equal("", wizard.TokenError);
   }



   [Fact]
   public async Task TheAccountIsSavedAtThePlaceStepOneAccepted_WhateverTheChoiceIsSetToAfterwards()
   {
      AccountWizardViewModel wizard = ChoosingWizard();
      wizard.Name = "Forge";
      wizard.IsOnGitea = true;
      wizard.Server = Server;
      await AdvanceToStepAsync(wizard, 2);

      // As a binding could set them while step 1 is not on screen: no place is taken without step 1.
      wizard.IsOnGitea = false;
      wizard.Server = "https://elsewhere.example.test";
      wizard.Token = "gitea_token";
      await AdvanceToStepAsync(wizard, 3);
      wizard.Organization = "platform";
      await AdvanceToStepAsync(wizard, 4);
      await wizard.SaveAsync();

      Assert.Equal([("git.example.test", "gitea_token")], _sent);
      Account saved = Assert.Single(new AccountsStore(_vault, _root).GetAll());
      Assert.Equal(ForgeKind.Gitea, saved.Forge);
      Assert.Equal(Server, saved.Server);
   }



   [Fact]
   public async Task APlaceThatNoListerCanBeMadeFor_IsNotTaken()
   {
      var wizard = new AccountWizardViewModel(
          _store,
          place => place.Server is null ? ListerOf(place) : throw new InvalidOperationException("no lister for that place"),
          _defaults);
      wizard.Name = "Forge";
      wizard.Token = "ghp_typed_for_github";
      wizard.IsOnGitea = true;
      wizard.Server = Server;

      await Assert.ThrowsAsync<InvalidOperationException>(wizard.TryAdvanceAsync);

      // The wizard is where it was: on GitHub, with GitHub's lister and the token typed for GitHub.
      Assert.Equal(1, wizard.Step);
      Assert.Equal("GitHub", wizard.PlaceName);
      Assert.Equal("ghp_typed_for_github", wizard.Token);

      wizard.IsOnGitea = false;
      await AdvanceToStepAsync(wizard, 3);

      Assert.Equal([("GitHub", "ghp_typed_for_github")], _sent);
   }



   [Fact]
   public async Task WhileATokenIsBeingChecked_TheWizardDoesNotMove()
   {
      var answer = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
      var wizard = new AccountWizardViewModel(
          _store,
          place => new FakeOrganizationLister
          {
             Handler = (token, _) =>
             {
                _sent.Enqueue((place.DisplayName, token));
                return answer.Task;
             },
          },
          _defaults);
      wizard.Name = "Work";
      wizard.Token = "ghp_token";
      await AdvanceToStepAsync(wizard, 2);

      Task<bool> checking = wizard.TryAdvanceAsync();
      Assert.True(wizard.IsValidatingToken);

      // Neither back, nor on: the answer that is on its way is for this place and this token.
      wizard.GoBack();
      Assert.Equal(2, wizard.Step);
      Task<bool> again = wizard.TryAdvanceAsync();
      Assert.True(again.IsCompleted, "a second Next started another check instead of being refused");
      Assert.False(await again);
      Assert.Equal(2, wizard.Step);
      Assert.Single(_sent);

      answer.SetResult(["me", "acme"]);

      Assert.True(await checking);
      Assert.Equal(3, wizard.Step);
      Assert.Equal(["me", "acme"], wizard.Organizations);

      wizard.GoBack(); // and it moves again
      Assert.Equal(2, wizard.Step);
   }



   [Fact]
   public async Task TheMessageAboutTheServer_GoesAway_WhenTheAddressIsCorrected()
   {
      AccountWizardViewModel wizard = ChoosingWizard();
      wizard.Name = "Forge";
      wizard.IsOnGitea = true;
      wizard.Server = "http://git.example.test";
      Assert.False(await wizard.TryAdvanceAsync());
      Assert.Equal("The server address cannot be used: it has to begin with https://.", wizard.ServerError);

      wizard.Server = "https://git.example.test";

      Assert.Equal("", wizard.ServerError);
   }



   // ---------------------------------------------------------------- the default token is a GitHub token

   [Fact]
   public async Task TheDefaultToken_IsOnOfferForGitHub_AndNeverSentToAGiteaServer()
   {
      AccountWizardViewModel wizard = ChoosingWizard(defaultToken: "ghp_default");
      Assert.True(wizard.OffersTokenChoice);
      Assert.True(wizard.UseDefaultToken);
      var changed = new List<string?>();
      wizard.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

      wizard.Name = "Forge";
      wizard.IsOnGitea = true;
      wizard.Server = Server;
      await AdvanceToStepAsync(wizard, 2);

      Assert.False(wizard.OffersTokenChoice);
      Assert.False(wizard.UseDefaultToken);
      Assert.Contains(nameof(AccountWizardViewModel.OffersTokenChoice), changed, StringComparer.Ordinal);

      Assert.False(await wizard.TryAdvanceAsync()); // no token of its own was typed
      Assert.Equal("Enter a token to continue.", wizard.TokenError);
      Assert.Empty(_sent);

      // The choice is not shown here. Set all the same, it is refused: nothing is sent.
      wizard.TokenChoiceIndex = 0;
      Assert.False(await wizard.TryAdvanceAsync());
      Assert.Equal("The default token is a GitHub token. This account needs a token of its own.", wizard.TokenError);
      Assert.Empty(_sent);

      // Back on GitHub, it is on offer again, and preselected as for any new account.
      wizard.GoBack();
      wizard.IsOnGitea = false;
      await AdvanceToStepAsync(wizard, 3);

      Assert.True(wizard.OffersTokenChoice);
      Assert.True(wizard.UseDefaultToken);
      Assert.Equal("", wizard.TokenError);
      Assert.Equal([("GitHub", "ghp_default")], _sent);
   }



   // ---------------------------------------------------------------- when there is no choice

   [Fact]
   public async Task AWizardThatWasGivenOneLister_OffersNoChoice_AndItsAccountIsOnGitHub()
   {
      var onlyLister = new FakeOrganizationLister();
      var wizard = new AccountWizardViewModel(_store, onlyLister, _defaults);

      Assert.False(wizard.CanChoosePlace);
      Assert.Equal("This account is on GitHub.", wizard.PlaceNote);

      // Whatever is set on it, it asks for no server and stays where its lister is.
      wizard.IsOnGitea = true;
      wizard.Server = Server;
      Assert.False(wizard.AsksForServer);

      wizard.Name = "Work";
      wizard.Token = "ghp_token";
      await AdvanceToStepAsync(wizard, 3);
      wizard.Organization = "acme";
      await AdvanceToStepAsync(wizard, 4);
      await wizard.SaveAsync();

      Assert.Equal(1, onlyLister.Calls);
      Account saved = Assert.Single(new AccountsStore(_vault, _root).GetAll());
      Assert.Equal(ForgeKind.GitHub, saved.Forge);
      Assert.Null(saved.Server);
   }



   [Fact]
   public void AQuickSyncConnectionSavedAsAnAccount_IsOnGitHub_AndOffersNoChoice()
   {
      var seed = new AccountWizardSeed("ghp_token", "acme", @"C:\repos", CreateOrgSubfolder: false, MaxConcurrency: 4);

      var wizard = new AccountWizardViewModel(_store, new FakeOrganizationLister(), seed);

      Assert.False(wizard.CanChoosePlace);
      Assert.Equal("This account is on GitHub.", wizard.PlaceNote);
      Assert.Equal(new Uri("https://github.com/settings/tokens"), wizard.TokenPage);
   }



   [Fact]
   public void EditingAnAccountOnAGiteaServer_SaysWhereItIs_AndThatItStaysThere()
   {
      Account account = GiteaAccount();

      var wizard = new AccountWizardViewModel(_store, new FakeOrganizationLister(), _defaults, account, () => "gitea_token");

      Assert.False(wizard.CanChoosePlace);
      Assert.False(wizard.AsksForServer);
      Assert.Equal("", wizard.Server); // what is stored is not put into a box
      Assert.Equal(
          "This account is on git.example.test. That cannot be changed: its token was saved for that place.",
          wizard.PlaceNote);
      Assert.Equal("Next checks the token with https://git.example.test/ and lists the organizations it can see.", wizard.TokenStepNote);
      Assert.Equal("git.example.test", wizard.PlaceName);
      Assert.Equal("Access token", wizard.TokenHeader);
      Assert.Equal("Leave empty to keep the stored token", wizard.TokenPlaceholder);
      Assert.Equal(new Uri("https://git.example.test/user/settings/applications"), wizard.TokenPage);
   }



   [Fact]
   public void EditingAGitHubAccount_SaysItIsOnGitHub()
   {
      Account account = GiteaAccount(server: null) with { Forge = ForgeKind.GitHub, TokenSource = TokenSource.Default };

      var wizard = new AccountWizardViewModel(_store, new FakeOrganizationLister(), _defaults, account, () => null);

      Assert.Equal("", wizard.Server);
      Assert.Equal("This account is on GitHub. That cannot be changed: its token was saved for that place.", wizard.PlaceNote);
      Assert.Equal("ghp_…", wizard.TokenPlaceholder); // it has no stored token of its own to keep
   }



   [Fact]
   public void EditingAnAccountWhoseStoredAddressCannotBeUsed_SaysSo_AndPointsAtNoPage()
   {
      Account account = GiteaAccount(server: "http://git.example.test"); // as a file edited by hand could say

      var wizard = new AccountWizardViewModel(_store, new FakeOrganizationLister(), _defaults, account, () => "gitea_token");

      Assert.Equal(
          "This account is on Gitea, at an address that cannot be used. Delete the account and add it again.",
          wizard.PlaceNote);
      Assert.Equal("Gitea, at an address that cannot be used", wizard.PlaceName);
      Assert.Null(wizard.TokenPage);
      Assert.Equal("Access token", wizard.TokenHeader);
   }



   [Fact]
   public void EditingAnAccountOnAForgeThisVersionDoesNotKnow_SaysWhichVersionToUse()
   {
      Account account = GiteaAccount() with { ForgeName = "GitLab" }; // as a later version of gclo could have added it

      var wizard = new AccountWizardViewModel(_store, new FakeOrganizationLister(), _defaults, account, () => "a_token");

      Assert.Equal(
          "This account is on a forge this version of gclo does not know. Use the version of gclo that added it.",
          wizard.PlaceNote);
      Assert.Null(wizard.TokenPage);
      Assert.False(wizard.OffersTokenChoice);
   }



   // ---------------------------------------------------------------- arguments

   [Fact]
   public void AWizardThatMayChoose_NeedsItsListersAndItsDefaults()
   {
      Assert.Throws<ArgumentNullException>(
          () => new AccountWizardViewModel(_store, (Func<ForgeEndpoint, IOrganizationLister>)null!, _defaults));
      Assert.Throws<ArgumentNullException>(() => new AccountWizardViewModel(_store, ListerOf, null!));
   }



   // ---------------------------------------------------------------- an account's page

   [Fact]
   public void AWorkspace_NamesThePlaceItConnectsTo()
   {
      using var quickSync = new WorkspaceViewModel(new FakeRepositoryLister(), new FakeGitClient(), new FakeOrganizationLister());
      using var onGitea = new WorkspaceViewModel(
          new FakeRepositoryLister(), new FakeGitClient(), new FakeOrganizationLister(), account: GiteaAccount(), tokenVault: _vault);
      using var onGitHub = new WorkspaceViewModel(
          new FakeRepositoryLister(), new FakeGitClient(), new FakeOrganizationLister(),
          account: GiteaAccount(server: null) with { Forge = ForgeKind.GitHub }, tokenVault: _vault);
      using var unusable = new WorkspaceViewModel(
          new FakeRepositoryLister(), new FakeGitClient(), new FakeOrganizationLister(),
          account: GiteaAccount(server: "http://git.example.test"), tokenVault: _vault);

      Assert.Equal("Connect to GitHub", quickSync.ConnectTitle);
      Assert.Equal(new Uri("https://github.com/settings/tokens"), quickSync.TokenPage);
      Assert.Equal("Personal access token", quickSync.TokenHeader);

      Assert.Equal("Connect to git.example.test", onGitea.ConnectTitle);
      Assert.Equal(new Uri("https://git.example.test/user/settings/applications"), onGitea.TokenPage);
      Assert.Equal("Access token", onGitea.TokenHeader);

      Assert.Equal("Connect to GitHub", onGitHub.ConnectTitle);
      Assert.Equal(new Uri("https://github.com/settings/tokens"), onGitHub.TokenPage);

      // What is stored cannot be used: the address is not repeated, and there is no page.
      Assert.Equal("Connect to Gitea, at an address that cannot be used", unusable.ConnectTitle);
      Assert.Null(unusable.TokenPage);
   }
}
