/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;
using gclo.ViewModels;


namespace gclo.Engine.Tests;


/// <summary>
/// Quick Sync's place, chosen on its card (#209): GitHub, or Gitea and the server's
/// address. The rules these tests are built around: nothing is sent anywhere
/// before the user chose a place and asked (a token that is typed, pasted or filled
/// in goes nowhere by itself); a token is handed only to the listers of the place
/// the card is on; and what was entered or loaded for one place is not carried to
/// another. Each lister here records what it was given
/// under the place it is the lister of. Also what the app remembers of a Quick Sync
/// connection between runs (never a token), and which workspace it opens on.
/// </summary>
public sealed class QuickSyncPlaceTests : IDisposable
{
   private const string Server = "https://git.example.test/";

   private static readonly ForgeEndpoint s_gitea = Place(Server);

   private readonly string _root = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

   /// <summary>Every token a lister was given: the place the lister is for, what was asked, and the token.</summary>
   private readonly ConcurrentQueue<(string Place, string Asked, string Token)> _sent = new();

   private readonly List<WorkspaceViewModel> _workspaces = [];

   /// <summary>How many organizations a lister lists; one means "only your own account".</summary>
   private int _organizations = 2;



   public void Dispose()
   {
      foreach(WorkspaceViewModel workspace in _workspaces)
      {
         workspace.Dispose();
      }
      GitTestHelpers.TryDeleteDirectory(_root);
   }



   private static ForgeEndpoint Place(string server)
   {
      Assert.True(ForgeEndpoint.TryCreate(ForgeKind.Gitea, server, out ForgeEndpoint? place, out _));
      return place;
   }



   /// <summary>The listers of a place: they record what they are given and answer with things named after the place.</summary>
   private (IRepositoryLister Repositories, IOrganizationLister Organizations) ListersOf(ForgeEndpoint place)
   {
      var repositories = new RecordingRepositoryLister(place.DisplayName, _sent);
      var organizations = new FakeOrganizationLister
      {
         Handler = (token, _) =>
         {
            _sent.Enqueue((place.DisplayName, "organizations", token));
            IReadOnlyList<string> listed = _organizations == 1 ? ["me"] : ["me", $"org-at-{place.DisplayName}"];
            return Task.FromResult(listed);
         },
      };
      return (repositories, organizations);
   }



   private WorkspaceViewModel QuickSync(
       Func<ForgeEndpoint, string?>? defaultTokenOf = null,
       Func<ForgeEndpoint, (IRepositoryLister Repositories, IOrganizationLister Organizations)>? listersOf = null)
   {
      var workspace = new WorkspaceViewModel(
          git: new FakeGitClient(),
          orgLookupDebounce: TimeSpan.FromMilliseconds(1),
          log: new NullActivityLog(),
          wsl: new FakeWslCloner(),
          listersOf: listersOf ?? ListersOf,
          defaultTokenOf: defaultTokenOf);
      _workspaces.Add(workspace);
      return workspace;
   }



   /// <summary>Long enough for a lookup that was going to be sent to have been sent (the debounce is 1 ms).</summary>
   private static Task ALookupWouldHaveGoneOutAsync() => Task.Delay(150);



   /// <summary>A Quick Sync on which GitHub was chosen.</summary>
   private WorkspaceViewModel QuickSyncOnGitHub(Func<ForgeEndpoint, string?>? defaultTokenOf = null)
   {
      WorkspaceViewModel workspace = QuickSync(defaultTokenOf);
      workspace.PlaceIndex = 0;
      return workspace;
   }



   /// <summary>Types a token and asks for the organizations it lists, as the Find button does.</summary>
   private static async Task TypeTokenAsync(WorkspaceViewModel workspace, string token)
   {
      workspace.Token = token;
      Assert.True(workspace.FindOrganizationsCommand.CanExecute(null));
      await workspace.FindOrganizationsCommand.ExecuteAsync(null);
      Assert.NotEmpty(workspace.Organizations);
   }



   // ---------------------------------------------------------------- the choice

   [Fact]
   public async Task QuickSync_StartsWithNoPlaceChosen_AndNothingCanBeSent()
   {
      WorkspaceViewModel workspace = QuickSync(defaultTokenOf: _ => "ghp_default_0123456789");

      Assert.True(workspace.CanChoosePlace);
      Assert.False(workspace.IsPlaceChosen);
      Assert.Equal(-1, workspace.PlaceIndex);
      Assert.False(workspace.IsOnGitea);
      Assert.False(workspace.AsksForServer);
      Assert.Equal("Connect", workspace.ConnectTitle);
      Assert.Equal("", workspace.PlaceName);
      Assert.Equal("Access token", workspace.TokenHeader);
      Assert.Equal("", workspace.TokenPlaceholder);
      Assert.Null(workspace.TokenPage);
      Assert.False(workspace.SaveAsAccountOffered);

      // No place was chosen: no default token is filled in, and one that is put in the box goes nowhere.
      workspace.FillInTheDefaultToken();
      Assert.Equal("", workspace.Token);

      workspace.Token = "a-token-0123456789";
      workspace.Organization = "typed-by-hand";
      workspace.Server = Server; // an address without the choice is no place either
      await ALookupWouldHaveGoneOutAsync();

      Assert.Empty(_sent);
      Assert.False(workspace.IsPlaceChosen);
      Assert.False(workspace.FindOrganizationsCommand.CanExecute(null));
      Assert.False(workspace.LoadReposCommand.CanExecute(null));
   }



   [Fact]
   public void ChoosingGitHub_IsAChoiceToo()
   {
      WorkspaceViewModel workspace = QuickSync(defaultTokenOf: place => place.Kind == ForgeKind.GitHub ? "ghp_default_0123456789" : null);
      var changed = new List<string?>();
      workspace.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

      workspace.PlaceIndex = 0;

      Assert.True(workspace.IsPlaceChosen);
      Assert.Equal(0, workspace.PlaceIndex);
      Assert.False(workspace.IsOnGitea);
      Assert.Equal("Connect to GitHub", workspace.ConnectTitle);
      Assert.Equal("Personal access token", workspace.TokenHeader);
      Assert.Equal("ghp_…", workspace.TokenPlaceholder);
      Assert.Equal(new Uri("https://github.com/settings/tokens"), workspace.TokenPage);
      Assert.Contains(nameof(WorkspaceViewModel.IsPlaceChosen), changed, StringComparer.Ordinal);
      Assert.Contains(nameof(WorkspaceViewModel.ConnectTitle), changed, StringComparer.Ordinal);

      // The default token saved for the place that was chosen is filled in. It is not sent.
      Assert.Equal("ghp_default_0123456789", workspace.Token);
      Assert.Empty(_sent);
   }



   [Fact]
   public async Task ATokenThatIsTypedPastedOrFilledIn_GoesNowhere_UntilTheUserAsks()
   {
      WorkspaceViewModel workspace = QuickSyncOnGitHub();

      workspace.Token = "ghp_token_0123456789";
      workspace.Organization = "typed-by-hand";
      await ALookupWouldHaveGoneOutAsync();

      Assert.Empty(_sent);
      Assert.Empty(workspace.Organizations);

      // Find: the organizations are asked for, now, of the place that is on the card.
      await workspace.FindOrganizationsCommand.ExecuteAsync(null);

      Assert.Equal([("GitHub", "organizations", "ghp_token_0123456789")], _sent);
      Assert.Equal(["me", "org-at-GitHub"], workspace.Organizations);
      Assert.Equal("typed-by-hand", workspace.Organization); // what was typed stays

      // Another token: the list of the other one goes, and again nothing is sent by itself.
      workspace.Token = "ghp_other_0123456789";
      await ALookupWouldHaveGoneOutAsync();

      Assert.Empty(workspace.Organizations);
      Assert.Single(_sent);

      // Too short to be a token: there is nothing to ask with.
      workspace.Token = "ghp_short";
      Assert.False(workspace.FindOrganizationsCommand.CanExecute(null));
   }



   [Fact]
   public void PlaceIndex_IsTheChoiceAsARadioGroupReadsIt_AndNoSelectionChangesNothing()
   {
      WorkspaceViewModel workspace = QuickSync();
      var changed = new List<string?>();
      workspace.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

      workspace.PlaceIndex = -1; // a radio group with no selection: still nothing chosen
      Assert.False(workspace.IsPlaceChosen);

      workspace.PlaceIndex = 1;

      Assert.True(workspace.IsOnGitea);
      Assert.True(workspace.AsksForServer);
      Assert.Equal(1, workspace.PlaceIndex);
      Assert.Contains(nameof(WorkspaceViewModel.PlaceIndex), changed, StringComparer.Ordinal);
      Assert.Contains(nameof(WorkspaceViewModel.AsksForServer), changed, StringComparer.Ordinal);

      workspace.PlaceIndex = -1; // a radio group that has no selection for a moment

      Assert.True(workspace.IsOnGitea);

      workspace.PlaceIndex = 0;

      Assert.False(workspace.IsOnGitea);
      Assert.False(workspace.AsksForServer);
   }



   [Fact]
   public async Task Gitea_WithoutAnAddressYet_IsNoPlace_AndNothingIsSent()
   {
      WorkspaceViewModel workspace = QuickSync();

      workspace.IsOnGitea = true;

      Assert.Equal("Connect to Gitea", workspace.ConnectTitle);
      Assert.Equal("Access token", workspace.TokenHeader);
      Assert.Equal("", workspace.TokenPlaceholder);
      Assert.Null(workspace.TokenPage); // no server, no page of it
      Assert.Equal("", workspace.ServerError); // nothing was entered yet: nothing to complain about

      workspace.Token = "a-token-typed-too-early";
      workspace.Organization = "typed-by-hand";
      await ALookupWouldHaveGoneOutAsync();

      Assert.Empty(_sent);
      Assert.Empty(workspace.Organizations);
      Assert.False(workspace.LoadReposCommand.CanExecute(null));
   }



   // ---------------------------------------------------------------- where the token goes

   [Fact]
   public async Task AConnectionToAGiteaServer_SendsItsTokenThere_AndNowhereElse()
   {
      WorkspaceViewModel workspace = QuickSync();
      QuickSyncConnection? connected = null;
      workspace.Connected = connection => connected = connection;
      var changed = new List<string?>();
      workspace.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

      workspace.IsOnGitea = true;
      workspace.Server = "  git.example.test  "; // as it is typed

      // The address reads as it was normalized: it is the one a token goes to.
      Assert.Equal(Server, workspace.Server);
      Assert.Equal("", workspace.ServerError);
      Assert.Equal("Connect to git.example.test", workspace.ConnectTitle);
      Assert.Equal(new Uri("https://git.example.test/user/settings/applications"), workspace.TokenPage);
      Assert.All(
          (string[])
          [
              nameof(WorkspaceViewModel.Server), nameof(WorkspaceViewModel.PlaceName), nameof(WorkspaceViewModel.ConnectTitle),
              nameof(WorkspaceViewModel.TokenHeader), nameof(WorkspaceViewModel.TokenPlaceholder), nameof(WorkspaceViewModel.TokenPage),
          ],
          name => Assert.Contains(name, changed, StringComparer.Ordinal));
      Assert.Empty(_sent);

      await TypeTokenAsync(workspace, "gitea-token-0123456789");

      Assert.Equal(["me", "org-at-git.example.test"], workspace.Organizations);
      Assert.Equal("Found 2 organizations and accounts.", workspace.StatusText);

      workspace.Organization = "org-at-git.example.test";
      workspace.TargetFolder = @"C:\mirror";
      workspace.CreateOrgSubfolder = true;
      await workspace.LoadReposCommand.ExecuteAsync(null);

      Assert.Equal(
          [("git.example.test", "organizations", "gitea-token-0123456789"), ("git.example.test", "repositories", "gitea-token-0123456789")],
          _sent);
      Assert.True(workspace.HasLoadedRepos);

      // The connection works: offered as an account, and told to whoever remembers it. No token in what is told.
      Assert.True(workspace.SaveAsAccountOffered);
      Assert.Equal(new QuickSyncConnection(s_gitea, "org-at-git.example.test", @"C:\mirror", CreateOrgSubfolder: true), connected);
   }



   [Theory]
   [InlineData("http://git.example.test", "The server address cannot be used: it has to begin with https://.")]
   [InlineData(
       "https://someone:s3cret-token@git.example.test",
       "The server address cannot be used: it contains a user name or a password; the token is entered separately.")]
   public async Task AnAddressThatCannotBeUsed_IsSaidSo_AndNothingIsSent(string typed, string said)
   {
      WorkspaceViewModel workspace = QuickSync();
      workspace.IsOnGitea = true;

      workspace.Server = typed;
      workspace.Token = "gitea-token-0123456789";
      workspace.Organization = "typed-by-hand";
      await ALookupWouldHaveGoneOutAsync();

      Assert.Equal(said, workspace.ServerError);
      Assert.DoesNotContain("s3cret", workspace.ServerError, StringComparison.Ordinal); // a refused address is not repeated
      Assert.Equal(typed, workspace.Server); // left as it was typed, to be corrected
      Assert.Equal("Connect to Gitea", workspace.ConnectTitle);
      Assert.Empty(_sent);
      Assert.False(workspace.LoadReposCommand.CanExecute(null));

      // Corrected, the message goes away and the place is taken. What was typed before there was a place is not kept for it.
      workspace.Server = "git.example.test";

      Assert.Equal("", workspace.ServerError);
      Assert.Equal("Connect to git.example.test", workspace.ConnectTitle);
      Assert.Equal("", workspace.Token);
      Assert.False(workspace.LoadReposCommand.CanExecute(null));

      workspace.Token = "gitea-token-0123456789";
      workspace.Organization = "typed-by-hand";

      Assert.True(workspace.LoadReposCommand.CanExecute(null));
      Assert.True(workspace.FindOrganizationsCommand.CanExecute(null));
      Assert.Empty(_sent);
   }



   [Fact]
   public async Task TheOrganizationHint_IsInTheWordsOfTheForge()
   {
      _organizations = 1; // a token that sees no organization
      WorkspaceViewModel onGitHub = QuickSyncOnGitHub();
      WorkspaceViewModel onGitea = QuickSync();
      onGitea.IsOnGitea = true;
      onGitea.Server = Server;

      onGitHub.Token = "ghp_token_0123456789";
      onGitea.Token = "gitea-token-0123456789";
      await onGitHub.FindOrganizationsCommand.ExecuteAsync(null);
      await onGitea.FindOrganizationsCommand.ExecuteAsync(null);

      Assert.Equal(
          "Only your personal account is visible — add read:org (classic) for organizations, or type an org name manually.",
          onGitHub.StatusText);
      Assert.Equal(
          "Only your personal account is visible — allow the token read:organization for organizations, or type an org name manually.",
          onGitea.StatusText);
   }



   // ---------------------------------------------------------------- nothing is carried to another place

   [Fact]
   public async Task WhatWasEnteredAndLoadedForOnePlace_IsForgotten_WhenAnotherIsChosen()
   {
      WorkspaceViewModel workspace = QuickSyncOnGitHub();
      await TypeTokenAsync(workspace, "ghp_token_0123456789");
      workspace.Organization = "org-at-GitHub";
      await workspace.LoadReposCommand.ExecuteAsync(null);
      Assert.Equal(2, workspace.Repos.Count);
      Assert.True(workspace.SaveAsAccountOffered);

      // The other kind of forge: everything of GitHub goes at once, before there is an address.
      workspace.IsOnGitea = true;

      Assert.Equal("", workspace.Token);
      Assert.Equal("", workspace.Organization);
      Assert.Empty(workspace.Organizations);
      Assert.Empty(workspace.Repos); // their clone addresses are on github.com: not to be synced with a token of the server
      Assert.Empty(workspace.FilteredRepos);
      Assert.False(workspace.HasLoadedRepos);
      Assert.Null(workspace.LoadedOrganization);
      Assert.False(workspace.SaveAsAccountOffered);
      Assert.False(workspace.SyncCommand.CanExecute(null));

      workspace.Server = Server;
      await TypeTokenAsync(workspace, "gitea-token-0123456789");
      workspace.Organization = "org-at-git.example.test";
      await workspace.LoadReposCommand.ExecuteAsync(null);
      int sentSoFar = _sent.Count;

      // From one server to another: the same.
      workspace.Server = "https://other.example.test";

      Assert.Equal("", workspace.Token);
      Assert.Equal("", workspace.Organization);
      Assert.Empty(workspace.Repos);
      Assert.Equal("Connect to other.example.test", workspace.ConnectTitle);
      await ALookupWouldHaveGoneOutAsync();
      Assert.Equal(sentSoFar, _sent.Count); // nothing went to the new server: no token was typed for it

      Assert.DoesNotContain(_sent, sent => string.Equals(sent.Place, "git.example.test", StringComparison.Ordinal) && sent.Token.StartsWith("ghp_", StringComparison.Ordinal));
      Assert.DoesNotContain(_sent, sent => string.Equals(sent.Place, "GitHub", StringComparison.Ordinal) && sent.Token.StartsWith("gitea", StringComparison.Ordinal));
   }



   [Fact]
   public async Task TheSameServer_SpelledAnotherWay_IsTheSamePlace_AndNothingIsForgotten()
   {
      WorkspaceViewModel workspace = QuickSync();
      workspace.IsOnGitea = true;
      workspace.Server = "git.example.test";
      await TypeTokenAsync(workspace, "gitea-token-0123456789");
      workspace.Organization = "org-at-git.example.test";

      workspace.Server = "HTTPS://GIT.example.test";

      Assert.Equal(Server, workspace.Server);
      Assert.Equal("gitea-token-0123456789", workspace.Token);
      Assert.Equal("org-at-git.example.test", workspace.Organization);
   }



   [Fact]
   public async Task APlaceWhoseListersCannotBeMade_IsNotTaken_AndNothingGoesToTheListersOfTheOther()
   {
      WorkspaceViewModel workspace = QuickSync(
          listersOf: place => string.Equals(place.Server?.Host, "broken.example.test", StringComparison.Ordinal) ? throw new InvalidOperationException("no listers for that place") : ListersOf(place));
      workspace.IsOnGitea = true;
      workspace.Server = Server;
      await TypeTokenAsync(workspace, "gitea-token-0123456789");
      int sentSoFar = _sent.Count;

      Assert.Throws<InvalidOperationException>(() => workspace.Server = "https://broken.example.test");

      // The card shows the new address, and there are no listers for it: the token that is still there can go nowhere.
      workspace.Token = "gitea-token-0123456789-again";
      workspace.Organization = "typed-by-hand";

      Assert.False(workspace.FindOrganizationsCommand.CanExecute(null));
      Assert.False(workspace.LoadReposCommand.CanExecute(null));
      Assert.Equal(sentSoFar, _sent.Count);
   }



   [Fact]
   public async Task WhileTheWorkspaceIsBusy_ThePlaceStaysAsItIs()
   {
      var gate = new TaskCompletionSource<IReadOnlyList<RepoDescriptor>>(TaskCreationOptions.RunContinuationsAsynchronously);
      WorkspaceViewModel workspace = QuickSync(listersOf: place => (new GatedRepositoryLister(gate.Task), ListersOf(place).Organizations));
      workspace.PlaceIndex = 0;
      await TypeTokenAsync(workspace, "ghp_token_0123456789");
      workspace.Organization = "org-at-GitHub";
      Task loading = workspace.LoadReposCommand.ExecuteAsync(null);
      Assert.False(workspace.CanEditInputs);
      var changed = new List<string?>();
      workspace.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

      workspace.IsOnGitea = true;
      workspace.Server = Server;

      // Refused, and said: a control that asked shows the choice as it is.
      Assert.False(workspace.IsOnGitea);
      Assert.Equal("", workspace.Server);
      Assert.Contains(nameof(WorkspaceViewModel.IsOnGitea), changed, StringComparer.Ordinal);
      Assert.Contains(nameof(WorkspaceViewModel.Server), changed, StringComparer.Ordinal);
      Assert.Equal("ghp_token_0123456789", workspace.Token);

      gate.SetResult([new RepoDescriptor("alpha", "https://github.com/org-at-GitHub/alpha.git", "main", false)]);
      await loading;

      Assert.Single(workspace.Repos);
   }



   [Fact]
   public async Task TheAnswerToALookup_ThatArrivesAfterAnotherPlaceWasChosen_IsNotShown()
   {
      var answer = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
      WorkspaceViewModel workspace = QuickSync(listersOf: place => place.Server is not null
          ? ListersOf(place)
          : (ListersOf(place).Repositories, new FakeOrganizationLister { Handler = (_, _) => answer.Task }));
      workspace.PlaceIndex = 0;
      workspace.Token = "ghp_token_0123456789";
      Task asking = workspace.FindOrganizationsCommand.ExecuteAsync(null);

      workspace.IsOnGitea = true;
      workspace.Server = Server;
      answer.SetResult(["me", "an-organization-on-github"]);
      await asking;

      Assert.Empty(workspace.Organizations); // GitHub's organizations are not offered for the server
      Assert.False(workspace.IsLoadingOrgs);
   }



   // ---------------------------------------------------------------- the default token of a place

   [Fact]
   public async Task TheDefaultTokenOfAPlace_IsFilledInForThatPlaceOnly()
   {
      var asked = new List<string>();
      WorkspaceViewModel workspace = QuickSync(defaultTokenOf: place =>
      {
         asked.Add(place.DisplayName);
         return place.Kind == ForgeKind.GitHub ? "ghp_default_0123456789" : null;
      });

      workspace.PlaceIndex = 0;

      Assert.Equal("ghp_default_0123456789", workspace.Token);
      await ALookupWouldHaveGoneOutAsync();
      Assert.Empty(_sent); // filled in, not sent

      // On a Gitea server there is none: the box is empty, and GitHub's default token goes nowhere near it.
      workspace.IsOnGitea = true;
      workspace.Server = Server;
      await ALookupWouldHaveGoneOutAsync();

      Assert.Equal("", workspace.Token);
      Assert.Contains("git.example.test", asked, StringComparer.Ordinal);
      Assert.DoesNotContain(_sent, sent => string.Equals(sent.Place, "git.example.test", StringComparison.Ordinal));

      // Back on GitHub it is filled in again.
      workspace.IsOnGitea = false;

      Assert.Equal("ghp_default_0123456789", workspace.Token);
   }



   [Fact]
   public void TheDefaultToken_DoesNotReplaceATokenThatWasTyped_AndIsNotForAnAccount()
   {
      WorkspaceViewModel workspace = QuickSync(defaultTokenOf: _ => "ghp_default_0123456789");
      workspace.Token = "ghp_typed_0123456789";

      workspace.PlaceIndex = 0;
      workspace.FillInTheDefaultToken();

      Assert.Equal("ghp_typed_0123456789", workspace.Token);

      var account = new Account { Id = Guid.NewGuid(), Name = "Work", Organization = "acme", TargetRoot = @"C:\repos" };
      using var ofAnAccount = new WorkspaceViewModel(
          new FakeRepositoryLister(), new FakeGitClient(), new FakeOrganizationLister(), account: account,
          listersOf: ListersOf, defaultTokenOf: _ => "ghp_default_0123456789");

      ofAnAccount.FillInTheDefaultToken();
      ofAnAccount.Restore(new QuickSyncConnection(s_gitea, "platform", @"C:\mirror", CreateOrgSubfolder: false));
      ofAnAccount.IsOnGitea = true;
      ofAnAccount.Server = Server;

      // An account's place and token are its own: none of this is for it.
      Assert.False(ofAnAccount.CanChoosePlace);
      Assert.Equal("", ofAnAccount.Token);
      Assert.False(ofAnAccount.IsOnGitea);
      Assert.Equal("", ofAnAccount.Server);
      Assert.Equal("acme", ofAnAccount.Organization);
      Assert.Equal("Connect to GitHub", ofAnAccount.ConnectTitle);
   }



   [Fact]
   public void AChangeOfTheDefaultTokenInSettings_ReachesQuickSync_OnlyWhileItIsOnGitHub()
   {
      WorkspaceViewModel workspace = QuickSync();

      workspace.OnDefaultTokenChanged(previous: null, current: "ghp_default_0123456789");
      Assert.Equal("", workspace.Token); // no place was chosen yet

      workspace.PlaceIndex = 0;
      workspace.OnDefaultTokenChanged(previous: null, current: "ghp_default_0123456789");
      Assert.Equal("ghp_default_0123456789", workspace.Token);

      workspace.IsOnGitea = true;
      workspace.Server = Server;
      workspace.OnDefaultTokenChanged(previous: null, current: "ghp_another_0123456789"); // the box is empty, as "previous" says

      Assert.Equal("", workspace.Token); // a GitHub token: not for this place

      workspace.Server = "http://not-usable";
      workspace.OnDefaultTokenChanged(previous: null, current: "ghp_another_0123456789");

      Assert.Equal("", workspace.Token);
   }



   // ---------------------------------------------------------------- opening the way it was left

   [Fact]
   public async Task Restore_PutsTheCardBackAsAConnectionHadIt_WithoutAToken_AndSendsNothing()
   {
      WorkspaceViewModel workspace = QuickSync();

      workspace.Restore(new QuickSyncConnection(s_gitea, "platform", @"C:\mirror", CreateOrgSubfolder: true));
      await ALookupWouldHaveGoneOutAsync();

      Assert.True(workspace.IsOnGitea);
      Assert.Equal(Server, workspace.Server);
      Assert.Equal("Connect to git.example.test", workspace.ConnectTitle);
      Assert.Equal("platform", workspace.Organization);
      Assert.Equal(@"C:\mirror", workspace.TargetFolder);
      Assert.True(workspace.CreateOrgSubfolder);
      Assert.Equal("", workspace.Token);
      Assert.False(workspace.HasLoadedRepos); // nothing is loaded until the user asks
      Assert.Empty(_sent);
   }



   [Fact]
   public void Restore_OfAGitHubConnection_FillsInTheDefaultToken_AndLeavesAFolderThatWasNotRememberedAlone()
   {
      WorkspaceViewModel workspace = QuickSync(defaultTokenOf: place => place.Kind == ForgeKind.GitHub ? "ghp_default_0123456789" : null);
      workspace.TargetFolder = @"C:\from-settings";

      workspace.Restore(new QuickSyncConnection(ForgeEndpoint.GitHub, "acme", TargetFolder: "", CreateOrgSubfolder: true));

      Assert.False(workspace.IsOnGitea);
      Assert.Equal("acme", workspace.Organization);
      Assert.Equal(@"C:\from-settings", workspace.TargetFolder);
      Assert.False(workspace.CreateOrgSubfolder);
      Assert.Equal("ghp_default_0123456789", workspace.Token);
      Assert.Throws<ArgumentNullException>(() => workspace.Restore(null!));
   }



   [Fact]
   public void AQuickSyncThatWasGivenNoListersOfAPlace_OffersNoChoice()
   {
      using var workspace = new WorkspaceViewModel(new FakeRepositoryLister(), new FakeGitClient(), new FakeOrganizationLister());
      var changed = new List<string?>();
      workspace.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

      workspace.IsOnGitea = true;
      workspace.Server = Server;
      workspace.Restore(new QuickSyncConnection(s_gitea, "platform", @"C:\mirror", CreateOrgSubfolder: false));

      Assert.False(workspace.CanChoosePlace);
      Assert.True(workspace.IsPlaceChosen); // its place is given: GitHub
      Assert.Equal(0, workspace.PlaceIndex);
      Assert.False(workspace.IsOnGitea);
      Assert.False(workspace.AsksForServer);
      Assert.Equal("", workspace.Server);
      Assert.Equal("", workspace.Organization);
      Assert.Equal("Connect to GitHub", workspace.ConnectTitle);
      Assert.Contains(nameof(WorkspaceViewModel.IsOnGitea), changed, StringComparer.Ordinal); // said all the same: the control shows what is
   }



   // ---------------------------------------------------------------- saved as an account

   [Fact]
   public async Task AConnectionSavedAsAnAccount_IsAnAccountOnThePlaceItWasOn()
   {
      WorkspaceViewModel workspace = QuickSync();
      workspace.IsOnGitea = true;
      workspace.Server = Server;
      await TypeTokenAsync(workspace, "gitea-token-0123456789");
      workspace.Organization = "org-at-git.example.test";
      workspace.TargetFolder = @"C:\mirror";

      AccountWizardSeed seed = workspace.ToAccountSeed();

      Assert.Equal(s_gitea, seed.Place);
      Assert.Equal("gitea-token-0123456789", seed.Token);
      Assert.DoesNotContain("gitea-token", seed.ToString(), StringComparison.Ordinal);
      Assert.Contains("Place = git.example.test", seed.ToString(), StringComparison.Ordinal);
      Assert.Contains("Place = GitHub", (seed with { Place = null }).ToString(), StringComparison.Ordinal);

      // The wizard that saves it: on that place, with that place's lister, and never on the default token (a GitHub token).
      var vault = new InMemoryVault();
      var store = new AccountsStore(vault, _root);
      var wizard = new AccountWizardViewModel(
          store, ListersOf(s_gitea).Organizations, seed, defaultToken: () => "gitea-token-0123456789");
      Assert.False(wizard.CanChoosePlace);
      Assert.Equal("This account is on git.example.test.", wizard.PlaceNote);
      Assert.False(wizard.UseDefaultToken);
      Assert.False(wizard.OffersTokenChoice);

      wizard.Name = "Platform";
      while(wizard.Step < 4)
      {
         Assert.True(await wizard.TryAdvanceAsync(), $"stopped at step {wizard.Step}");
      }
      await wizard.SaveAsync();

      Account saved = Assert.Single(new AccountsStore(vault, _root).GetAll());
      Assert.Equal(ForgeKind.Gitea, saved.Forge);
      Assert.Equal(Server, saved.Server);
      Assert.Equal("gitea-token-0123456789", vault.TryRetrieve(saved.TokenEntryId));
      Assert.DoesNotContain(_sent, sent => string.Equals(sent.Place, "GitHub", StringComparison.Ordinal));
   }



   [Fact]
   public async Task AnAccountsPage_IsNotOfferedToBeSavedAsAnAccount()
   {
      var account = new Account { Id = Guid.NewGuid(), Name = "Work", Organization = "acme", TargetRoot = @"C:\repos" };
      var vault = new InMemoryVault();
      vault.Store(account.Id, "ghp_token_0123456789");
      using var workspace = new WorkspaceViewModel(
          new FakeRepositoryLister { Repositories = [new RepoDescriptor("alpha", "https://github.com/acme/alpha.git", "main", false)] },
          new FakeGitClient(), new FakeOrganizationLister(), orgLookupDebounce: TimeSpan.FromMilliseconds(1), log: new NullActivityLog(),
          account: account, tokenVault: vault);
      bool told = false;
      workspace.Connected = _ => told = true;

      await workspace.LoadReposCommand.ExecuteAsync(null);

      Assert.True(workspace.HasLoadedRepos);
      Assert.False(workspace.SaveAsAccountOffered);
      Assert.False(told);
   }



   // ---------------------------------------------------------------- what is remembered between runs

   [Fact]
   public void AQuickSyncConnection_IsRememberedWithoutAToken_AndGivenBackAsItWas()
   {
      Directory.CreateDirectory(_root);
      var settings = new AppSettings();
      settings.RememberQuickSync(new QuickSyncConnection(s_gitea, " platform ", @" C:\mirror ", CreateOrgSubfolder: true));
      settings.LastWorkspace = StartWorkspace.QuickSync;
      Assert.True(settings.Save(directory: _root));

      var loaded = AppSettings.Load(directory: _root);

      Assert.Equal(new QuickSyncConnection(s_gitea, "platform", @"C:\mirror", CreateOrgSubfolder: true), loaded.RememberedQuickSync());
      Assert.Equal(StartWorkspace.QuickSync, loaded.LastWorkspace);
      string file = File.ReadAllText(Path.Combine(_root, "settings.json"));
      Assert.Contains("\"QuickSyncForge\": \"Gitea\"", file, StringComparison.Ordinal);
      Assert.Contains("\"QuickSyncServer\": \"https://git.example.test/\"", file, StringComparison.Ordinal);

      // GitHub is remembered as no forge and no server.
      loaded.RememberQuickSync(new QuickSyncConnection(ForgeEndpoint.GitHub, "acme", "", CreateOrgSubfolder: false));

      Assert.Null(loaded.QuickSyncForge);
      Assert.Null(loaded.QuickSyncServer);
      Assert.Equal(new QuickSyncConnection(ForgeEndpoint.GitHub, "acme", "", CreateOrgSubfolder: false), loaded.RememberedQuickSync());
      Assert.Throws<ArgumentNullException>(() => loaded.RememberQuickSync(null!));
   }



   [Fact]
   public void OnAFirstRun_NothingIsRemembered_SoNoPlaceIsChosenForTheUser()
   {
      var settings = new AppSettings();

      Assert.Null(settings.RememberedQuickSync());
      Assert.Null(settings.LastWorkspace);
   }



   [Theory]
   [InlineData("Gitea", "http://git.example.test")] // a file edited by hand
   [InlineData("Gitea", null)]
   [InlineData("GitLab", "https://gitlab.example.test/")] // a forge of a later version
   [InlineData(null, "https://git.example.test/")] // GitHub has no server
   public void WhatIsRemembered_ThatNamesNoUsablePlace_IsNotGivenBack(string? forge, string? server)
   {
      var settings = new AppSettings { QuickSyncForge = forge, QuickSyncServer = server, QuickSyncOrganization = "platform" };

      Assert.Null(settings.RememberedQuickSync());
   }



   [Fact]
   public void ASettingsFileThatSaysNullForWhatIsRemembered_ReadsAsNothing()
   {
      Directory.CreateDirectory(_root);
      File.WriteAllText(Path.Combine(_root, "settings.json"), """{ "QuickSyncOrganization": null, "QuickSyncTargetFolder": null }""");

      var loaded = AppSettings.Load(directory: _root);

      Assert.Equal("", loaded.QuickSyncOrganization);
      Assert.Equal("", loaded.QuickSyncTargetFolder);
   }



   // ---------------------------------------------------------------- which workspace the app opens on

   [Fact]
   public void TheAppOpens_WhereItWasLeft_AndOtherwiseOnTheFirstAccount_OrQuickSync()
   {
      Account first = new() { Id = Guid.NewGuid(), Name = "Alpha", Organization = "a", TargetRoot = "t" };
      Account second = new() { Id = Guid.NewGuid(), Name = "Beta", Organization = "b", TargetRoot = "t" };

      // A first run, and any run without accounts: Quick Sync.
      Assert.Equal(StartWorkspace.QuickSync, StartWorkspace.Choose([], last: null));
      Assert.Equal(StartWorkspace.QuickSync, StartWorkspace.Choose([], last: second.Id)); // the account that was open is gone

      // Accounts, and nothing recorded yet: the first of them.
      Assert.Equal(first.Id, StartWorkspace.Choose([first, second], last: null));
      Assert.Equal(first.Id, StartWorkspace.Choose([first], last: null));

      // Where it was left: an account, or Quick Sync.
      Assert.Equal(second.Id, StartWorkspace.Choose([first, second], last: second.Id));
      Assert.Equal(StartWorkspace.QuickSync, StartWorkspace.Choose([first, second], last: StartWorkspace.QuickSync));

      // The account that was open last was removed: the first of those that are left.
      Assert.Equal(first.Id, StartWorkspace.Choose([first], last: second.Id));
      Assert.Throws<ArgumentNullException>(() => StartWorkspace.Choose(null!, last: null));
   }



   // ---------------------------------------------------------------- the default token saved for a place

   [Fact]
   public void TheDefaultTokenInSettings_IsGitHubs_AndNoneForAnotherPlace()
   {
      var vault = new InMemoryVault();

      Assert.Null(DefaultTokens.For(vault, ForgeEndpoint.GitHub));

      vault.Store(AppSettings.DefaultTokenVaultId, "ghp_default_0123456789");

      Assert.Equal("ghp_default_0123456789", DefaultTokens.For(vault, ForgeEndpoint.GitHub));
      Assert.Null(DefaultTokens.For(vault, s_gitea));
      Assert.Throws<ArgumentNullException>(() => DefaultTokens.For(null!, ForgeEndpoint.GitHub));
      Assert.Throws<ArgumentNullException>(() => DefaultTokens.For(vault, null!));
   }



   // ---------------------------------------------------------------- fakes

   /// <summary>Lists two repositories on the place it is for, and records the token it was given.</summary>
   private sealed class RecordingRepositoryLister(string place, ConcurrentQueue<(string Place, string Asked, string Token)> sent) : IRepositoryLister
   {
      public Task<IReadOnlyList<RepoDescriptor>> ListOrganizationRepositoriesAsync(
          string organization, string token, CancellationToken cancellationToken = default)
      {
         sent.Enqueue((place, "repositories", token));
         IReadOnlyList<RepoDescriptor> listed =
         [
             new("alpha", $"https://{place}/{organization}/alpha.git", "main", IsArchived: false),
             new("bravo", $"https://{place}/{organization}/bravo.git", "main", IsArchived: false),
         ];
         return Task.FromResult(listed);
      }
   }



   /// <summary>Answers when the test lets it.</summary>
   private sealed class GatedRepositoryLister(Task<IReadOnlyList<RepoDescriptor>> answer) : IRepositoryLister
   {
      public Task<IReadOnlyList<RepoDescriptor>> ListOrganizationRepositoriesAsync(
          string organization, string token, CancellationToken cancellationToken = default)
          => answer;
   }
}
