/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.ViewModels;


namespace gclo.Engine.Tests;


/// <summary>
/// A default token per forge (#212): Settings holds one for GitHub and one for a
/// Gitea server. The rule these tests are built around replaces "the default token
/// is a GitHub token": a default token belongs to one place and is never used for
/// another. Quick Sync fills in the one of the place on its card, an account that
/// uses the default token gets the one of its own place, and the Gitea one is
/// saved, replaced and removed together with the address of its server.
/// </summary>
public sealed class DefaultTokenPerPlaceTests : IDisposable
{
   private const string Server = "https://git.example.test/";

   private const string OtherServer = "https://other.example.test/";

   private static readonly ForgeEndpoint s_gitea = Place(Server);

   private static readonly ForgeEndpoint s_other = Place(OtherServer);

   private readonly string _root = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

   private readonly InMemoryVault _vault = new();



   public void Dispose() => GitTestHelpers.TryDeleteDirectory(_root);



   private static ForgeEndpoint Place(string server)
   {
      Assert.True(ForgeEndpoint.TryCreate(ForgeKind.Gitea, server, out ForgeEndpoint? place, out _));
      return place;
   }



   private AccountsStore NewStore() => new(_vault, _root);



   private static Account AccountOn(ForgeEndpoint place, string name, TokenSource source = TokenSource.Default) => new()
   {
      Id = Guid.NewGuid(),
      Name = name,
      Organization = "acme",
      TargetRoot = @"C:\repos",
      Forge = place.Kind,
      Server = place.Server?.AbsoluteUri,
      TokenSource = source,
   };



   // ---------------------------------------------------------------- where a default token is kept

   [Fact]
   public void TheEntryOfADefaultToken_IsGitHubsAsEver_AndForAGiteaServerMadeOfItsAddress()
   {
      Assert.Equal(AppSettings.DefaultTokenVaultId, DefaultTokens.EntryFor(ForgeEndpoint.GitHub));

      // Pinned: tokens that are saved are found again by this value.
      Assert.Equal(new Guid("18734666-9532-7707-54df-1f8d61342bc1"), DefaultTokens.EntryFor(s_gitea));
      Assert.NotEqual(DefaultTokens.EntryFor(s_gitea), DefaultTokens.EntryFor(s_other));
      Assert.Throws<ArgumentNullException>(() => DefaultTokens.EntryFor(null!));
   }



   [Fact]
   public void ADefaultToken_IsFoundForThePlaceItWasSavedFor_AndForNoOther()
   {
      Assert.Null(DefaultTokens.For(_vault, ForgeEndpoint.GitHub));
      Assert.Null(DefaultTokens.For(_vault, s_gitea));

      _vault.Store(DefaultTokens.EntryFor(ForgeEndpoint.GitHub), "ghp_default");
      _vault.Store(DefaultTokens.EntryFor(s_gitea), "gitea_default");

      Assert.Equal("ghp_default", DefaultTokens.For(_vault, ForgeEndpoint.GitHub));
      Assert.Equal("gitea_default", DefaultTokens.For(_vault, s_gitea));
      Assert.Null(DefaultTokens.For(_vault, s_other));
      Assert.Throws<ArgumentNullException>(() => DefaultTokens.For(null!, ForgeEndpoint.GitHub));

      _vault.Store(DefaultTokens.EntryFor(s_other), "");
      Assert.Null(DefaultTokens.For(_vault, s_other)); // an empty entry is no token
   }



   // ---------------------------------------------------------------- an account on the default token

   [Fact]
   public void AnAccountOnTheDefaultToken_GetsTheOneOfItsOwnPlace_AndNeverAnothers()
   {
      _vault.Store(DefaultTokens.EntryFor(ForgeEndpoint.GitHub), "ghp_default");
      _vault.Store(DefaultTokens.EntryFor(s_gitea), "gitea_default");
      var resolver = new AccountTokenResolver(_vault);

      Assert.Equal("ghp_default", resolver.TryResolve(AccountOn(ForgeEndpoint.GitHub, "Work")));
      Assert.Equal("gitea_default", resolver.TryResolve(AccountOn(s_gitea, "Forge")));

      // A server that has no default token gets none: neither GitHub's nor the other server's.
      Account elsewhere = AccountOn(s_other, "Elsewhere");
      Assert.Null(resolver.TryResolve(elsewhere));
      Assert.Equal(
          "uses the default token for other.example.test, but none is saved for it (desktop app: Settings)",
          resolver.MissingDescription(elsewhere));

      // An account whose place cannot be used has no place to have a default token of.
      Assert.Null(resolver.TryResolve(AccountOn(s_gitea, "Broken") with { Server = "http://git.example.test" }));
      Assert.Null(resolver.TryResolve(AccountOn(s_gitea, "Later") with { ForgeName = "GitLab" }));

      Assert.True(resolver.HasDefaultTokenFor(ForgeEndpoint.GitHub));
      Assert.True(resolver.HasDefaultTokenFor(s_gitea));
      Assert.False(resolver.HasDefaultTokenFor(s_other));

      _vault.Delete(DefaultTokens.EntryFor(ForgeEndpoint.GitHub));
      Assert.Equal(
          "uses the default token, but no default token is saved (desktop app: Settings)",
          resolver.MissingDescription(AccountOn(ForgeEndpoint.GitHub, "Work")));
   }



   [Fact]
   public void AnAccountOnAGiteaServer_CanBeSavedAsUsingTheDefaultToken_AndIsCountedForItsPlaceOnly()
   {
      AccountsStore store = NewStore();
      Account work = AccountOn(ForgeEndpoint.GitHub, "Work");
      Account forge = AccountOn(s_gitea, "Forge");
      Account elsewhere = AccountOn(s_other, "Elsewhere");
      Account own = AccountOn(s_gitea, "Own", TokenSource.Own);
      store.Save(work, token: null);
      store.Save(forge, token: null);
      store.Save(elsewhere, token: null);
      store.Save(own, "gitea_own");

      Assert.Equal(TokenSource.Default, NewStore().FindByName("Forge")!.TokenSource);
      Assert.Equal(["Work"], store.UsingDefaultTokenOf(ForgeEndpoint.GitHub).Select(a => a.Name), StringComparer.Ordinal);
      Assert.Equal(["Forge"], store.UsingDefaultTokenOf(s_gitea).Select(a => a.Name), StringComparer.Ordinal);
      Assert.Equal(["Elsewhere"], store.UsingDefaultTokenOf(s_other).Select(a => a.Name), StringComparer.Ordinal);
      Assert.Throws<ArgumentNullException>(() => store.UsingDefaultTokenOf(null!));
   }



   // ---------------------------------------------------------------- Settings: the Gitea group

   [Fact]
   public void WithNothingSaved_TheGiteaGroup_SavesAnAddressAndItsTokenTogether()
   {
      var state = new SettingsGiteaState(savedPlace: null, hasSavedToken: false, dependentAccounts: []);

      Assert.False(state.HasSavedToken);
      Assert.Equal("", state.InitialServer);
      Assert.Equal("", state.InitialBoxValue);
      Assert.False(state.CanOfferRemoval(""));

      // Nothing entered: nothing to do.
      Assert.Equal(new SettingsGiteaPlan(SettingsTokenAction.Keep, null, ""), state.Plan("", ""));
      Assert.Equal("No default token saved for a Gitea server.", state.Caption("", ""));
      Assert.False(state.IsWarning("", ""));

      // An address alone is no default.
      Assert.Equal(SettingsTokenAction.Keep, state.Plan("git.example.test", "").Action);
      Assert.False(state.Plan("git.example.test", "").IsError);

      // A token without an address has no server to be for.
      SettingsGiteaPlan noAddress = state.Plan("  ", "gitea_typed");
      Assert.True(noAddress.IsError);
      Assert.Equal("Enter the address of the server this token is for.", noAddress.Error);
      Assert.Equal(noAddress.Error, state.Caption("  ", "gitea_typed"));
      Assert.True(state.IsWarning("  ", "gitea_typed"));

      // An address that cannot be used is said so, and not repeated.
      SettingsGiteaPlan refused = state.Plan("https://bot:hunter2@git.example.test", "gitea_typed");
      Assert.True(refused.IsError);
      Assert.Equal("The server address cannot be used: it contains a user name or a password; the token is entered separately.", refused.Error);

      // Both: stored for that server, as its address is normalized.
      SettingsGiteaPlan store = state.Plan("git.example.test", "gitea_typed");
      Assert.Equal(new SettingsGiteaPlan(SettingsTokenAction.Store, s_gitea, ""), store);
      Assert.Equal(
          "The default token for git.example.test will be saved on Save; Quick Sync fills it in when it is on that server.",
          state.Caption("git.example.test", "gitea_typed"));

      var settings = new AppSettings();
      Assert.Equal("default token for git.example.test stored", state.Apply(store, "gitea_typed", _vault, settings));
      Assert.Equal(Server, settings.DefaultGiteaServer);
      Assert.Equal(s_gitea, settings.DefaultGiteaPlace());
      Assert.Equal("gitea_typed", DefaultTokens.For(_vault, s_gitea));
      Assert.Null(DefaultTokens.For(_vault, ForgeEndpoint.GitHub)); // GitHub's is another entry
   }



   [Fact]
   public void WithATokenSaved_TheGiteaGroup_KeepsReplacesAndRemovesIt_ForItsServer()
   {
      _vault.Store(DefaultTokens.EntryFor(s_gitea), "gitea_saved");
      var settings = new AppSettings { DefaultGiteaServer = Server };
      var state = new SettingsGiteaState(s_gitea, hasSavedToken: true, dependentAccounts: ["Forge", "Mirror"]);

      Assert.True(state.HasSavedToken);
      Assert.Equal(Server, state.InitialServer);
      Assert.Equal(SettingsTokenState.Mask, state.InitialBoxValue); // never the token
      Assert.True(state.CanOfferRemoval(SettingsTokenState.Mask));
      Assert.False(state.CanOfferRemoval("typed"));

      // Untouched: kept, and it says who uses it.
      Assert.Equal(SettingsTokenAction.Keep, state.Plan(Server, SettingsTokenState.Mask).Action);
      Assert.Equal(
          "A default token is saved for git.example.test and used by Quick Sync and 2 accounts. Type to replace it; clear the box to remove it.",
          state.Caption(Server, SettingsTokenState.Mask));
      Assert.False(state.IsWarning(Server, SettingsTokenState.Mask));
      Assert.Null(state.Apply(state.Plan(Server, SettingsTokenState.Mask), SettingsTokenState.Mask, _vault, settings));
      Assert.Equal("gitea_saved", DefaultTokens.For(_vault, s_gitea));

      // The same server spelled another way is the same server.
      Assert.Equal(SettingsTokenAction.Keep, state.Plan("HTTPS://GIT.example.test", SettingsTokenState.Mask).Action);

      // Typed over: replaced, for the same server.
      SettingsGiteaPlan replace = state.Plan(Server, "gitea_new");
      Assert.Equal(new SettingsGiteaPlan(SettingsTokenAction.Store, s_gitea, ""), replace);
      Assert.Equal(
          "The default token will be replaced on Save. The 2 accounts that use it switch to the new token too.",
          state.Caption(Server, "gitea_new"));
      Assert.False(state.IsWarning(Server, "gitea_new"));
      Assert.Equal("default token for git.example.test replaced", state.Apply(replace, "gitea_new", _vault, settings));
      Assert.Equal("gitea_new", DefaultTokens.For(_vault, s_gitea));

      // The box cleared, or the address cleared: removed, with the accounts it breaks named.
      foreach((string server, string box) in ((string, string)[])[(Server, ""), ("", SettingsTokenState.Mask), ("", "")])
      {
         SettingsGiteaPlan remove = state.Plan(server, box);
         Assert.Equal(new SettingsGiteaPlan(SettingsTokenAction.Remove, s_gitea, ""), remove);
         Assert.Equal(
             "The default token will be removed on Save. 2 accounts use it and will stop syncing until a new one is saved: Forge, Mirror.",
             state.Caption(server, box));
         Assert.True(state.IsWarning(server, box));
      }

      Assert.Equal(
          "default token for git.example.test removed (2 account(s) depend on it)",
          state.Apply(state.Plan(Server, ""), "", _vault, settings));
      Assert.Null(DefaultTokens.For(_vault, s_gitea));
      Assert.Null(settings.DefaultGiteaServer);
      Assert.Null(settings.DefaultGiteaPlace());
   }



   [Fact]
   public void TheSavedGiteaToken_IsNeverCarriedToAnotherAddress()
   {
      _vault.Store(DefaultTokens.EntryFor(s_gitea), "gitea_saved");
      var settings = new AppSettings { DefaultGiteaServer = Server };
      var state = new SettingsGiteaState(s_gitea, hasSavedToken: true, dependentAccounts: ["Forge"]);

      // Another address with the saved token still in the box: refused, and Save is not offered.
      SettingsGiteaPlan refused = state.Plan(OtherServer, SettingsTokenState.Mask);
      Assert.True(refused.IsError);
      Assert.Equal(
          "The saved token is for git.example.test. Enter the token for other.example.test, or put the address back.",
          refused.Error);
      Assert.True(state.IsWarning(OtherServer, SettingsTokenState.Mask));
      Assert.Throws<InvalidOperationException>(() => state.Apply(refused, SettingsTokenState.Mask, _vault, settings));
      Assert.Equal("gitea_saved", DefaultTokens.For(_vault, s_gitea));
      Assert.Null(DefaultTokens.For(_vault, s_other));

      // With the token of the new server typed: stored for it, and the old server's is removed.
      SettingsGiteaPlan move = state.Plan(OtherServer, "other_typed");
      Assert.Equal(new SettingsGiteaPlan(SettingsTokenAction.Store, s_other, ""), move);
      Assert.Equal(
          "The default token for other.example.test will be saved on Save, and the one for git.example.test removed."
          + " 1 account uses it and will stop syncing until a new one is saved: Forge.",
          state.Caption(OtherServer, "other_typed"));
      Assert.True(state.IsWarning(OtherServer, "other_typed"));

      Assert.Equal("default token for other.example.test stored", state.Apply(move, "other_typed", _vault, settings));
      Assert.Equal("other_typed", DefaultTokens.For(_vault, s_other));
      Assert.Null(DefaultTokens.For(_vault, s_gitea));
      Assert.Equal(OtherServer, settings.DefaultGiteaServer);
   }



   [Fact]
   public void AGiteaTokenRemoved_ThatNoAccountUses_IsRemovedWithoutAWarning()
   {
      _vault.Store(DefaultTokens.EntryFor(s_gitea), "gitea_saved");
      var settings = new AppSettings { DefaultGiteaServer = Server };
      var state = new SettingsGiteaState(s_gitea, hasSavedToken: true, dependentAccounts: []);

      Assert.Equal("The default token will be removed on Save.", state.Caption(Server, ""));
      Assert.False(state.IsWarning(Server, ""));
      Assert.Equal("default token for git.example.test removed", state.Apply(state.Plan(Server, ""), "", _vault, settings));
   }



   [Fact]
   public void AServerThatIsRememberedWithoutItsToken_IsNoDefault()
   {
      // The address is in the settings, the vault holds nothing for it (it was removed by hand, or on another machine).
      var state = new SettingsGiteaState(s_gitea, hasSavedToken: false, dependentAccounts: []);

      Assert.False(state.HasSavedToken);
      Assert.Equal("", state.InitialServer);
      Assert.Equal(SettingsTokenAction.Keep, state.Plan("", "").Action);
   }



   [Fact]
   public void TheGiteaGroup_NeedsItsArguments()
   {
      var state = new SettingsGiteaState(savedPlace: null, hasSavedToken: false, dependentAccounts: []);
      var nothing = new SettingsGiteaPlan(SettingsTokenAction.Keep, null, "");

      Assert.Throws<ArgumentNullException>(() => state.Plan(null!, ""));
      Assert.Throws<ArgumentNullException>(() => state.Plan("", null!));
      Assert.Throws<ArgumentNullException>(() => state.Apply(null!, "", _vault, new AppSettings()));
      Assert.Throws<ArgumentNullException>(() => state.Apply(nothing, null!, _vault, new AppSettings()));
      Assert.Throws<ArgumentNullException>(() => state.Apply(nothing, "", null!, new AppSettings()));
      Assert.Throws<ArgumentNullException>(() => state.Apply(nothing, "", _vault, null!));
   }



   [Theory]
   [InlineData(null)]
   [InlineData("")]
   [InlineData("http://git.example.test")] // a file edited by hand
   public void ADefaultGiteaServerThatIsNoUsableAddress_IsNoPlace(string? stored)
   {
      Assert.Null(new AppSettings { DefaultGiteaServer = stored }.DefaultGiteaPlace());
   }



   // ---------------------------------------------------------------- the wizard

   [Fact]
   public async Task TheWizard_OffersTheDefaultTokenOfThePlaceThatIsChosen_AndUsesItThereOnly()
   {
      var sent = new List<(string Place, string Token)>();
      AccountsStore store = NewStore();
      var wizard = new AccountWizardViewModel(
          store,
          place => new FakeOrganizationLister
          {
             Handler = (token, _) =>
             {
                sent.Add((place.DisplayName, token));
                return Task.FromResult<IReadOnlyList<string>>(["platform"]);
             },
          },
          new AppSettings(),
          defaultTokenOf: place => place.Equals(s_gitea) ? "gitea_default" : place.Kind == ForgeKind.GitHub ? "ghp_default" : null);

      // On GitHub: GitHub's default token.
      Assert.True(wizard.HasDefaultToken);
      Assert.True(wizard.OffersTokenChoice);
      Assert.True(wizard.UseDefaultToken);

      // On a server that has none: no choice, and a token of its own is asked for.
      wizard.Name = "Forge";
      wizard.IsOnGitea = true;
      wizard.Server = OtherServer;
      Assert.True(await wizard.TryAdvanceAsync());
      Assert.False(wizard.HasDefaultToken);
      Assert.False(wizard.OffersTokenChoice);
      Assert.False(wizard.UseDefaultToken);
      wizard.TokenChoiceIndex = 0; // the choice is not shown here; set all the same, there is nothing to send
      Assert.False(await wizard.TryAdvanceAsync());
      Assert.Equal(
          "The default token is no longer saved. Save one in Settings, or use a different token for this account.",
          wizard.TokenError);
      Assert.Empty(sent);

      // On the server that has one: offered, preselected, checked there, and the account uses it.
      wizard.GoBack();
      wizard.Server = Server;
      Assert.True(await wizard.TryAdvanceAsync());
      Assert.True(wizard.HasDefaultToken);
      Assert.True(wizard.OffersTokenChoice);
      Assert.True(wizard.UseDefaultToken);
      Assert.Equal("Used by Quick Sync. Replacing it in Settings updates this account too.", wizard.DefaultTokenCaption);
      Assert.True(await wizard.TryAdvanceAsync());
      Assert.Equal([("git.example.test", "gitea_default")], sent);

      wizard.Organization = "platform";
      Assert.True(await wizard.TryAdvanceAsync());
      wizard.TargetRoot = @"C:\mirror";
      await wizard.SaveAsync();

      Account saved = Assert.Single(NewStore().GetAll());
      Assert.Equal(ForgeKind.Gitea, saved.Forge);
      Assert.Equal(TokenSource.Default, saved.TokenSource);
      Assert.Null(_vault.TryRetrieve(saved.TokenEntryId)); // it has no token of its own
   }



   [Fact]
   public void TheWizard_EditingAnAccountOnTheDefaultTokenOfItsServer_CountsTheOthersOnThatServer()
   {
      AccountsStore store = NewStore();
      Account forge = AccountOn(s_gitea, "Forge");
      store.Save(forge, token: null);
      store.Save(AccountOn(s_gitea, "Mirror"), token: null);
      store.Save(AccountOn(ForgeEndpoint.GitHub, "Work"), token: null); // on GitHub's default: not counted here

      var wizard = new AccountWizardViewModel(
          store, new FakeOrganizationLister(), new AppSettings(), forge, () => null, defaultToken: () => "gitea_default");

      Assert.True(wizard.UseDefaultToken);
      Assert.True(wizard.OffersTokenChoice);
      Assert.Equal("Used by Quick Sync and 1 other account. Replacing it in Settings updates this account too.", wizard.DefaultTokenCaption);
   }



   // ---------------------------------------------------------------- Quick Sync

   [Fact]
   public void QuickSync_IsToldOfAChangedDefaultToken_OnlyForThePlaceItIsOn()
   {
      using var workspace = new WorkspaceViewModel(
          git: new FakeGitClient(), orgLookupDebounce: TimeSpan.FromMilliseconds(1), log: new NullActivityLog(), wsl: new FakeWslCloner(),
          listersOf: _ => (new FakeRepositoryLister(), new FakeOrganizationLister()),
          defaultTokenOf: place => DefaultTokens.For(_vault, place));
      _vault.Store(DefaultTokens.EntryFor(s_gitea), "gitea_default");

      workspace.IsOnGitea = true;
      workspace.Server = Server;

      Assert.Equal("gitea_default", workspace.Token); // filled in for the server on the card

      workspace.OnDefaultTokenChanged(ForgeEndpoint.GitHub, previous: "gitea_default", current: "ghp_new");
      workspace.OnDefaultTokenChanged(s_other, previous: "gitea_default", current: "other_new");
      Assert.Equal("gitea_default", workspace.Token); // another place's default: not this card's

      workspace.OnDefaultTokenChanged(s_gitea, previous: "gitea_default", current: "gitea_new");
      Assert.Equal("gitea_new", workspace.Token);

      workspace.OnDefaultTokenChanged(s_gitea, previous: "gitea_new", current: null);
      Assert.Equal("", workspace.Token);
      Assert.Throws<ArgumentNullException>(() => workspace.OnDefaultTokenChanged(null!, null, null));
   }
}
