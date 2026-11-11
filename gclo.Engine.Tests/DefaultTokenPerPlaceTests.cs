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

      // Two servers on one host are two places: each has an entry of its own.
      ForgeEndpoint underAPath = Place("https://git.example.test/gitea/");
      Assert.NotEqual(DefaultTokens.EntryFor(s_gitea), DefaultTokens.EntryFor(underAPath));
      Assert.NotEqual(DefaultTokens.EntryFor(underAPath), DefaultTokens.EntryFor(Place("https://git.example.test/mirror/")));
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
          "The default token for https://git.example.test/ will be saved on Save; Quick Sync fills it in when it is on that server.",
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
          "A default token is saved for https://git.example.test/ and used by Quick Sync and 2 accounts. Type to replace it; clear the box to remove it.",
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

      // A token typed with the address taken out has no server to be for: nothing is stored, nothing removed.
      SettingsGiteaPlan noAddress = state.Plan("", "gitea_for_nowhere");
      Assert.True(noAddress.IsError);
      Assert.Equal("Enter the address of the server this token is for.", noAddress.Error);
      Assert.Throws<InvalidOperationException>(() => state.Apply(noAddress, "gitea_for_nowhere", _vault, settings));
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
          "The saved token is for https://git.example.test/. Enter the token for https://other.example.test/, or put the address back.",
          refused.Error);
      Assert.True(state.IsWarning(OtherServer, SettingsTokenState.Mask));
      Assert.Throws<InvalidOperationException>(() => state.Apply(refused, SettingsTokenState.Mask, _vault, settings));
      Assert.Equal("gitea_saved", DefaultTokens.For(_vault, s_gitea));
      Assert.Null(DefaultTokens.For(_vault, s_other));

      // With the token of the new server typed: stored for it, and the old server's is removed.
      SettingsGiteaPlan move = state.Plan(OtherServer, "other_typed");
      Assert.Equal(new SettingsGiteaPlan(SettingsTokenAction.Store, s_other, ""), move);
      Assert.Equal(
          "The default token for https://other.example.test/ will be saved on Save, and the one for https://git.example.test/ removed."
          + " 1 account uses it and will stop syncing until a new one is saved: Forge.",
          state.Caption(OtherServer, "other_typed"));
      Assert.True(state.IsWarning(OtherServer, "other_typed"));

      Assert.Equal("default token for other.example.test stored", state.Apply(move, "other_typed", _vault, settings));
      Assert.Equal("other_typed", DefaultTokens.For(_vault, s_other));
      Assert.Null(DefaultTokens.For(_vault, s_gitea));
      Assert.Equal(OtherServer, settings.DefaultGiteaServer);
   }



   [Fact]
   public void TwoServersOnOneHost_AreToldApartInWhatTheGroupSays()
   {
      ForgeEndpoint first = Place("https://git.example.test/first/");
      var state = new SettingsGiteaState(first, hasSavedToken: true, dependentAccounts: []);

      Assert.Equal(
          "The saved token is for https://git.example.test/first/. Enter the token for https://git.example.test/second/, or put the address back.",
          state.Plan("https://git.example.test/second", SettingsTokenState.Mask).Error);
      Assert.Equal(
          "The default token for https://git.example.test/second/ will be saved on Save, and the one for https://git.example.test/first/ removed.",
          state.Caption("https://git.example.test/second", "typed"));
   }



   [Fact]
   public void ATokenThatCannotBeStoredForAnotherServer_LeavesTheSavedOneWhereItWas()
   {
      _vault.Store(DefaultTokens.EntryFor(s_gitea), "gitea_saved");
      var settings = new AppSettings { DefaultGiteaServer = Server };
      var state = new SettingsGiteaState(s_gitea, hasSavedToken: true, dependentAccounts: ["Forge"]);
      var vault = new VaultThatCannotStore(_vault);

      Assert.Throws<IOException>(() => state.Apply(state.Plan(OtherServer, "other_typed"), "other_typed", vault, settings));

      // The new token was to be stored first: the server that had the default still has it, and is still the default.
      Assert.Equal(0, vault.Deleted);
      Assert.Equal("gitea_saved", DefaultTokens.For(_vault, s_gitea));
      Assert.Equal(Server, settings.DefaultGiteaServer);
   }



   [Fact]
   public void ATokenTypedBehindTheMask_IsStoredWithoutTheMask()
   {
      _vault.Store(DefaultTokens.EntryFor(s_gitea), "gitea_saved");
      var settings = new AppSettings { DefaultGiteaServer = Server };
      var state = new SettingsGiteaState(s_gitea, hasSavedToken: true, dependentAccounts: []);
      string box = SettingsTokenState.Mask + "gitea_new"; // typed into the box without emptying it first

      Assert.Equal(new SettingsGiteaPlan(SettingsTokenAction.Store, s_gitea, ""), state.Plan(Server, box));
      Assert.Equal("The default token will be replaced on Save.", state.Caption(Server, box));
      Assert.Equal("default token for git.example.test replaced", state.Apply(state.Plan(Server, box), box, _vault, settings));
      Assert.Equal("gitea_new", DefaultTokens.For(_vault, s_gitea));

      // A part of the mask deleted and nothing typed: no token was entered, so the saved one is removed, and it says so.
      Assert.Equal(SettingsTokenAction.Remove, state.Plan(Server, SettingsTokenState.Mask[1..]).Action);
      Assert.Equal("The default token will be removed on Save.", state.Caption(Server, SettingsTokenState.Mask[1..]));

      // The same with another address, or none: what is left of the mask is no token for that server either.
      Assert.True(state.Plan(OtherServer, SettingsTokenState.Mask[1..]).IsError);
      Assert.Equal(SettingsTokenAction.Remove, state.Plan("", SettingsTokenState.Mask[1..]).Action);
      Assert.Throws<ArgumentNullException>(() => state.Plan(Server, null!));
   }



   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public void ATokenThatIsGoneWhenSaveIsPressed_IsNotTakenUp_AndTheSavedDefaultIsNotGivenUpForIt(bool anotherIsSaved)
   {
      _vault.Store(DefaultTokens.EntryFor(s_other), "other_saved");
      _vault.Store(DefaultTokens.EntryFor(s_gitea), "gitea_lost");
      var settings = new AppSettings { DefaultGiteaServer = anotherIsSaved ? OtherServer : null };
      var state = new SettingsGiteaState(
          anotherIsSaved ? s_other : null, hasSavedToken: anotherIsSaved, dependentAccounts: [],
          holdsATokenFor: place => DefaultTokens.For(_vault, place) is not null);
      SettingsGiteaPlan takeUp = state.Plan(Server, "");
      Assert.True(takeUp.TakesUp);

      // Between the address being typed and Save, the token is removed elsewhere.
      _vault.Delete(DefaultTokens.EntryFor(s_gitea));

      Assert.Equal(
          "default token for git.example.test not taken up: it is no longer saved; nothing was changed",
          state.Apply(takeUp, "", _vault, settings));
      Assert.Equal(anotherIsSaved ? OtherServer : null, settings.DefaultGiteaServer);
      Assert.Equal("other_saved", DefaultTokens.For(_vault, s_other));
   }



   [Fact]
   public void ATokenTheVaultStillHolds_IsTakenUp_InPlaceOfTheDefaultThatIsSaved()
   {
      // The default is another server; the vault still holds a token for a server Settings lost sight of.
      _vault.Store(DefaultTokens.EntryFor(s_other), "other_saved");
      _vault.Store(DefaultTokens.EntryFor(s_gitea), "gitea_lost");
      var settings = new AppSettings { DefaultGiteaServer = OtherServer };
      var asked = new List<ForgeEndpoint>();
      var state = new SettingsGiteaState(
          s_other, hasSavedToken: true, dependentAccounts: ["Mirror"],
          holdsATokenFor: place =>
          {
             asked.Add(place);
             return DefaultTokens.For(_vault, place) is not null;
          });

      // A server that has no token: refused as before, the saved token is not carried there.
      Assert.True(state.Plan("https://third.example.test", SettingsTokenState.Mask).IsError);

      // The server whose token is there, with the box as it was or emptied: taken up, and it says what goes.
      foreach(string box in (string[])[SettingsTokenState.Mask, ""])
      {
         SettingsGiteaPlan takeUp = state.Plan(Server, box);
         Assert.Equal(new SettingsGiteaPlan(SettingsTokenAction.Keep, s_gitea, "", TakesUp: true), takeUp);
         Assert.Equal(
             "A default token is already saved for https://git.example.test/. Save makes this server the Gitea default again, "
             + "and removes the one for https://other.example.test/; open Settings afterwards to replace or remove the token."
             + " 1 account uses it and will stop syncing until a new one is saved: Mirror.",
             state.Caption(Server, box));
         Assert.True(state.IsWarning(Server, box));
      }

      // The vault was asked once for each server, however often the plan was made.
      Assert.Equal(2, asked.Count);

      Assert.Equal(
          "default token for git.example.test taken up again (it was already saved), and the one for other.example.test removed",
          state.Apply(state.Plan(Server, SettingsTokenState.Mask), SettingsTokenState.Mask, _vault, settings));
      Assert.Equal(Server, settings.DefaultGiteaServer);
      Assert.Equal("gitea_lost", DefaultTokens.For(_vault, s_gitea)); // the token itself is not touched
      Assert.Null(DefaultTokens.For(_vault, s_other)); // one default Gitea server

      // With no account on the saved default, taking another one up is no warning.
      var unused = new SettingsGiteaState(s_other, hasSavedToken: true, dependentAccounts: [], holdsATokenFor: _ => true);
      Assert.False(unused.IsWarning(Server, SettingsTokenState.Mask));
   }



   [Fact]
   public void ATokenTheVaultStillHolds_ForAServerSettingsLostSightOf_IsTakenUpAgain_WhenItsAddressIsEntered()
   {
      // settings.json no longer names the server (an older gclo wrote the file back); its token is still in the vault,
      // and is found by whatever asks for the default of that server.
      _vault.Store(DefaultTokens.EntryFor(s_gitea), "gitea_saved");
      var settings = new AppSettings();
      var state = new SettingsGiteaState(
          savedPlace: null, hasSavedToken: false, dependentAccounts: [], holdsATokenFor: place => DefaultTokens.For(_vault, place) is not null);

      Assert.False(state.HasSavedToken);
      Assert.Equal("", state.InitialServer);
      Assert.Equal("No default token saved for a Gitea server.", state.Caption("", ""));

      // The address of a server that has none: nothing to do.
      Assert.Equal(new SettingsGiteaPlan(SettingsTokenAction.Keep, null, ""), state.Plan(OtherServer, ""));
      Assert.Null(state.Apply(state.Plan(OtherServer, ""), "", _vault, settings));
      Assert.Null(settings.DefaultGiteaServer);

      // The address of the server whose token is there, and nothing typed: it is the default it was.
      SettingsGiteaPlan takeUp = state.Plan("git.example.test", "");
      Assert.Equal(new SettingsGiteaPlan(SettingsTokenAction.Keep, s_gitea, "", TakesUp: true), takeUp);
      Assert.False(takeUp.IsError);
      Assert.Equal(
          "A default token is already saved for https://git.example.test/. Save makes this server the Gitea default again; "
          + "open Settings afterwards to replace or remove the token.",
          state.Caption("git.example.test", ""));
      Assert.False(state.IsWarning("git.example.test", ""));

      Assert.Equal(
          "default token for git.example.test taken up again (it was already saved)",
          state.Apply(takeUp, "", _vault, settings));
      Assert.Equal(Server, settings.DefaultGiteaServer);
      Assert.Equal("gitea_saved", DefaultTokens.For(_vault, s_gitea)); // the token itself is not touched

      // The dialog that opens next sees it as saved, and can replace or remove it.
      var next = new SettingsGiteaState(settings.DefaultGiteaPlace(), hasSavedToken: true, dependentAccounts: []);
      Assert.Equal(Server, next.InitialServer);
      Assert.Equal(SettingsTokenAction.Remove, next.Plan(Server, "").Action);

      // With a token typed for that address: an ordinary save, which replaces what was there.
      Assert.Equal(new SettingsGiteaPlan(SettingsTokenAction.Store, s_gitea, ""), state.Plan("git.example.test", "gitea_new"));
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

      // A token that is saved is saved for a server.
      Assert.Throws<ArgumentException>(() => new SettingsGiteaState(savedPlace: null, hasSavedToken: true, dependentAccounts: []));
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
          store, new FakeOrganizationLister(), new AppSettings(), forge, () => null,
          defaultTokenOf: place => place.Equals(s_gitea) ? "gitea_default" : null);

      Assert.True(wizard.UseDefaultToken);
      Assert.True(wizard.HasDefaultToken);
      Assert.True(wizard.OffersTokenChoice);
      Assert.Equal("Used by Quick Sync and 1 other account. Replacing it in Settings updates this account too.", wizard.DefaultTokenCaption);
   }



   [Fact]
   public async Task AWizardForAnAccountOnAGiteaServer_ThatIsHandedGitHubsDefaultToken_OffersAndSendsNone()
   {
      // The older way to give a wizard "the default token" knows one token, GitHub's. It is no token of this server.
      var sent = new List<string>();
      var lister = new FakeOrganizationLister
      {
         Handler = (token, _) =>
         {
            sent.Add(token);
            return Task.FromResult<IReadOnlyList<string>>(["platform"]);
         },
      };
      AccountsStore store = NewStore();
      Account own = AccountOn(s_gitea, "Own", TokenSource.Own);
      store.Save(own, "gitea_own");

      var editing = new AccountWizardViewModel(
          store, lister, new AppSettings(), own, () => "gitea_own", defaultToken: () => "ghp_default");

      Assert.False(editing.HasDefaultToken);
      Assert.False(editing.OffersTokenChoice);
      Assert.False(editing.UseDefaultToken);

      // Set all the same (the choice is not shown): there is no default token for this server to send.
      editing.TokenChoiceIndex = 0;
      Assert.True(await editing.TryAdvanceAsync());
      Assert.False(await editing.TryAdvanceAsync());
      Assert.Equal(
          "The default token is no longer saved. Save one in Settings, or use a different token for this account.",
          editing.TokenError);
      Assert.Empty(sent);

      // A connection on that server, saved as an account, with the very token GitHub's default is: it is the
      // account's own token here, not "the default".
      var seed = new AccountWizardSeed("ghp_default", "platform", @"C:\mirror", CreateOrgSubfolder: true, MaxConcurrency: 4, s_gitea);
      var saving = new AccountWizardViewModel(store, lister, seed, defaultToken: () => "ghp_default");

      Assert.False(saving.HasDefaultToken);
      Assert.False(saving.UseDefaultToken);

      // The same source on GitHub is GitHub's default token, as it always was.
      var onGitHub = new AccountWizardViewModel(store, lister, seed with { Place = null }, defaultToken: () => "ghp_default");
      Assert.True(onGitHub.HasDefaultToken);
      Assert.True(onGitHub.UseDefaultToken);
   }



   [Fact]
   public void AWizardForAnAccountWhosePlaceCannotBeUsed_HasNoDefaultToken()
   {
      AccountsStore store = NewStore();
      Account later = AccountOn(s_gitea, "Later", TokenSource.Own) with { ForgeName = "GitLab" };
      var asked = new List<ForgeEndpoint>();

      var wizard = new AccountWizardViewModel(
          store, new FakeOrganizationLister(), new AppSettings(), later, () => null,
          defaultTokenOf: place =>
          {
             asked.Add(place);
             return "a_default";
          });

      // It is on no place this gclo knows: there is no place to ask the default token of, and none is asked for.
      Assert.False(wizard.HasDefaultToken);
      Assert.False(wizard.OffersTokenChoice);
      Assert.False(wizard.UseDefaultToken);
      Assert.Empty(asked);
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



   /// <summary>A vault that reads and removes as the one it wraps, and cannot store.</summary>
   private sealed class VaultThatCannotStore(InMemoryVault inner) : ITokenVault
   {
      public int Deleted { get; private set; }



      public void Store(Guid accountId, string token) => throw new IOException("the credential store is not available");



      public string? TryRetrieve(Guid accountId) => inner.TryRetrieve(accountId);



      public void Delete(Guid accountId)
      {
         Deleted++;
         inner.Delete(accountId);
      }



      public string DescribeEntry(Guid accountId) => inner.DescribeEntry(accountId);
   }
}
