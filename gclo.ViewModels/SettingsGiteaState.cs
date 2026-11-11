/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.Engine;


namespace gclo.ViewModels;


/// <summary>
/// The Gitea group of the Settings dialog, as state the dialog only renders: the
/// address of the server and its default token. It is the counterpart of
/// <see cref="SettingsTokenState"/> (the GitHub token), with the one thing a Gitea
/// default has on top: the token is for one server, so the address and the token
/// are saved, replaced and removed together.
/// </summary>
/// <remarks>
/// <para>
/// The saved token is never carried to another address. Changing the address
/// asks for the token of the new server; the token of the old one is removed.
/// </para>
/// <para>
/// Which server has the default is written in settings.json, the token is in the
/// vault under an entry named after that server. The two can come apart: the
/// settings file could not be written, or read, or an older gclo wrote it back
/// without the address. The token is then still found by everything that asks for
/// the default of that server, while this dialog shows none. So that it can be
/// seen and removed again, an address that is entered here and already has a
/// default token in the vault is taken up as the default it is. There is one
/// default Gitea server: when another one is saved, taking a server up removes
/// that one's token, as typing a token for another server does.
/// </para>
/// </remarks>
public sealed class SettingsGiteaState
{
   private readonly ForgeEndpoint? _savedPlace;

   private readonly SettingsTokenState _token;

   private readonly Func<ForgeEndpoint, bool> _holdsATokenFor;

   /// <summary>What <see cref="_holdsATokenFor"/> answered, by server: the vault is asked once for each.</summary>
   private readonly Dictionary<ForgeEndpoint, bool> _held = [];



   /// <summary>
   /// <paramref name="savedPlace"/>: the server a default token is saved for, or
   /// null; <paramref name="hasSavedToken"/>: whether the vault holds that token;
   /// <paramref name="dependentAccounts"/>: the names of the accounts on that
   /// server that use the default token, which a removal would break;
   /// <paramref name="holdsATokenFor"/>: whether the vault holds a default token for
   /// a server (see the remarks), none when it is not given.
   /// </summary>
   public SettingsGiteaState(
       ForgeEndpoint? savedPlace, bool hasSavedToken, IReadOnlyList<string> dependentAccounts, Func<ForgeEndpoint, bool>? holdsATokenFor = null)
   {
      if(hasSavedToken && savedPlace is null)
      {
         throw new ArgumentException("A token that is saved is saved for a server.", nameof(savedPlace));
      }

      _savedPlace = savedPlace;
      _token = new SettingsTokenState(hasSavedToken, dependentAccounts);
      _holdsATokenFor = holdsATokenFor ?? (_ => false);
   }



   /// <summary>True when a default token is saved for a server.</summary>
   public bool HasSavedToken => _token.HasSavedToken;

   /// <summary>What the address box shows at first: the saved server's address, or nothing.</summary>
   public string InitialServer => HasSavedToken ? _savedPlace?.Server?.AbsoluteUri ?? "" : "";

   /// <summary>What the token box shows at first: the mask when a token is saved, empty otherwise.</summary>
   public string InitialBoxValue => _token.InitialBoxValue;

   /// <summary>True when "Remove saved token" applies: a token is saved and the box still shows the mask.</summary>
   public bool CanOfferRemoval(string boxValue) => _token.CanOfferRemoval(boxValue);



   /// <summary>What Save will do with the address box and the token box as they stand.</summary>
   public SettingsGiteaPlan Plan(string server, string boxValue)
   {
      ArgumentNullException.ThrowIfNull(server);
      boxValue = SettingsTokenState.WithoutTheMask(boxValue);
      bool typed = boxValue.Length > 0 && !string.Equals(boxValue, SettingsTokenState.Mask, StringComparison.Ordinal);

      if(string.IsNullOrWhiteSpace(server))
      {
         if(typed)
         {
            return Refused("Enter the address of the server this token is for.");
         }

         // No address is no default: what was saved goes with it.
         return HasSavedToken ? new SettingsGiteaPlan(SettingsTokenAction.Remove, _savedPlace, "") : Nothing;
      }

      if(!ForgeEndpoint.TryCreate(ForgeKind.Gitea, server, out ForgeEndpoint? place, out string? reason))
      {
         return Refused(char.ToUpperInvariant(reason[0]) + reason[1..] + ".");
      }

      if(!HasSavedToken)
      {
         if(typed)
         {
            return new SettingsGiteaPlan(SettingsTokenAction.Store, place, "");
         }

         // Nothing typed: nothing to do, unless the vault holds a default token for this very server already.
         return HoldsATokenFor(place) ? new SettingsGiteaPlan(SettingsTokenAction.Keep, place, "", TakesUp: true) : Nothing;
      }

      if(place.Equals(_savedPlace))
      {
         SettingsTokenAction action = _token.Plan(boxValue);
         return action == SettingsTokenAction.Keep ? Nothing : new SettingsGiteaPlan(action, place, "");
      }

      ///////////////////////////////////////////////////////////////////////////////
      // Another server than the one the token was saved for. The saved token is   //
      // not sent there: the token of the new server has to be entered, unless the //
      // vault holds a default token for that very server already (see the         //
      // remarks), which is then taken up in place of the saved one.               //
      ///////////////////////////////////////////////////////////////////////////////
      if(typed)
      {
         return new SettingsGiteaPlan(SettingsTokenAction.Store, place, "");
      }

      return HoldsATokenFor(place)
          ? new SettingsGiteaPlan(SettingsTokenAction.Keep, place, "", TakesUp: true)
          : Refused($"The saved token is for {Address(_savedPlace!)}. Enter the token for {Address(place)}, or put the address back.");
   }



   /// <summary>
   /// Whether the vault holds a default token for <paramref name="place"/>. Asked
   /// once for each server, however often the boxes change: the plan is made again
   /// for every character that is typed.
   /// </summary>
   private bool HoldsATokenFor(ForgeEndpoint place)
   {
      if(!_held.TryGetValue(place, out bool held))
      {
         held = _holdsATokenFor(place);
         _held[place] = held;
      }
      return held;
   }



   /// <summary>
   /// A server as these captions name it: by its whole address, not its host
   /// alone. Two servers can share a host, and the captions tell one from the other.
   /// </summary>
   private static string Address(ForgeEndpoint place) => place.Server?.AbsoluteUri ?? place.DisplayName;



   /// <summary>The caption under the group for the boxes as they stand; names what Save will do.</summary>
   public string Caption(string server, string boxValue)
   {
      SettingsGiteaPlan plan = Plan(server, boxValue);
      if(plan.IsError)
      {
         return plan.Error;
      }

      switch(plan.Action)
      {
         case SettingsTokenAction.Store when !HasSavedToken:
            return $"The default token for {Address(plan.Place!)} will be saved on Save; Quick Sync fills it in when it is on that server.";
         case SettingsTokenAction.Store when plan.Place!.Equals(_savedPlace):
            return "The default token will be replaced on Save." + _token.RotationNote();
         case SettingsTokenAction.Store:
            return $"The default token for {Address(plan.Place!)} will be saved on Save, and the one for {Address(_savedPlace!)} removed."
                + _token.RemovalWarning();
         case SettingsTokenAction.Remove:
            return "The default token will be removed on Save." + _token.RemovalWarning();
         case SettingsTokenAction.Keep when plan.TakesUp && HasSavedToken:
            return $"A default token is already saved for {Address(plan.Place!)}. Save makes this server the Gitea default again, "
                + $"and removes the one for {Address(_savedPlace!)}; open Settings afterwards to replace or remove the token."
                + _token.RemovalWarning();
         case SettingsTokenAction.Keep when plan.TakesUp:
            return $"A default token is already saved for {Address(plan.Place!)}. Save makes this server the Gitea default again; "
                + "open Settings afterwards to replace or remove the token.";
         default:
            return HasSavedToken
                ? $"A default token is saved for {Address(_savedPlace!)}" + _token.UsedBy() + ". Type to replace it; clear the box to remove it."
                : "No default token saved for a Gitea server.";
      }
   }



   /// <summary>
   /// True when the caption is a warning: the group cannot be saved as it stands, or
   /// Save would take a token away that accounts depend on.
   /// </summary>
   public bool IsWarning(string server, string boxValue)
   {
      SettingsGiteaPlan plan = Plan(server, boxValue);
      bool takesTheSavedTokenAway = plan.Action == SettingsTokenAction.Remove
          || ((plan.Action == SettingsTokenAction.Store || plan.TakesUp) && HasSavedToken && !plan.Place!.Equals(_savedPlace));
      return plan.IsError || (takesTheSavedTokenAway && _token.DependentCount > 0);
   }



   /// <summary>
   /// Does what <paramref name="plan"/> says: stores or removes the token in
   /// <paramref name="vault"/> and records the server in <paramref name="settings"/>
   /// (the caller saves them). The token of a server that is replaced by another is
   /// removed. Returns what changed, for the log, without the token; null when
   /// nothing did.
   /// </summary>
   public string? Apply(SettingsGiteaPlan plan, string boxValue, ITokenVault vault, AppSettings settings)
   {
      ArgumentNullException.ThrowIfNull(plan);
      ArgumentNullException.ThrowIfNull(boxValue);
      ArgumentNullException.ThrowIfNull(vault);
      ArgumentNullException.ThrowIfNull(settings);
      if(plan.IsError)
      {
         throw new InvalidOperationException("The Gitea default cannot be saved as it stands: " + plan.Error);
      }

      switch(plan.Action)
      {
         case SettingsTokenAction.Store:
            // The new one first: if it cannot be stored, the old server keeps its token and stays the default.
            vault.Store(DefaultTokens.EntryFor(plan.Place!), SettingsTokenState.WithoutTheMask(boxValue));
            settings.DefaultGiteaServer = plan.Place!.Server!.AbsoluteUri;
            if(HasSavedToken && !plan.Place.Equals(_savedPlace))
            {
               vault.Delete(DefaultTokens.EntryFor(_savedPlace!));
            }
            return HasSavedToken && plan.Place.Equals(_savedPlace)
                ? $"default token for {plan.Place.DisplayName} replaced"
                : $"default token for {plan.Place.DisplayName} stored";
         case SettingsTokenAction.Remove:
            vault.Delete(DefaultTokens.EntryFor(_savedPlace!));
            settings.DefaultGiteaServer = null;
            return _token.DependentCount == 0
                ? $"default token for {_savedPlace!.DisplayName} removed"
                : $"default token for {_savedPlace!.DisplayName} removed ({_token.DependentCount} account(s) depend on it)";
         case SettingsTokenAction.Keep when plan.TakesUp:
            /////////////////////////////////////////////////////////////////////////
            // Looked for again, now: the plan was made when the address was typed, //
            // and the token may have been removed since (another window, Windows'  //
            // Credential Manager). Without it there is nothing to take up, and the //
            // default that is saved is not given up for it.                        //
            /////////////////////////////////////////////////////////////////////////
            if(DefaultTokens.For(vault, plan.Place!) is null)
            {
               return $"default token for {plan.Place!.DisplayName} not taken up: it is no longer saved; nothing was changed";
            }

            settings.DefaultGiteaServer = plan.Place!.Server!.AbsoluteUri;
            if(!HasSavedToken)
            {
               return $"default token for {plan.Place.DisplayName} taken up again (it was already saved)";
            }

            // One default Gitea server: the one it takes the place of goes, as when a token is typed for another server.
            vault.Delete(DefaultTokens.EntryFor(_savedPlace!));
            return $"default token for {plan.Place.DisplayName} taken up again (it was already saved), and the one for {_savedPlace!.DisplayName} removed";
         default:
            return null;
      }
   }



   private static SettingsGiteaPlan Nothing => new(SettingsTokenAction.Keep, null, "");



   private static SettingsGiteaPlan Refused(string why) => new(SettingsTokenAction.Keep, null, why);
}
