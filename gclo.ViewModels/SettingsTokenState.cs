/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.ViewModels;


/// <summary>
/// The Settings dialog's default-token box, as state the dialog only renders: what
/// the box shows, what the caption says, and what Save does with what the user left
/// in the box (#101). A saved token is represented by a fixed <see cref="Mask"/>,
/// never by the token itself, so the box reads as "something is saved" and a reveal
/// button can only ever reveal the mask or what the user typed this session.
/// </summary>
public sealed class SettingsTokenState
{
   /// <summary>What the box shows while a saved token is untouched. A constant, not the token.</summary>
   public const string Mask = "••••••••••••••••";

   private readonly IReadOnlyList<string> _dependentAccounts;



   /// <summary>
   /// <paramref name="hasSavedToken"/>: whether the vault holds a default token;
   /// <paramref name="dependentAccounts"/>: the names of the accounts that resolve
   /// to it (<see cref="TokenSource.Default"/>), which a removal would break.
   /// </summary>
   public SettingsTokenState(bool hasSavedToken, IReadOnlyList<string> dependentAccounts)
   {
      ArgumentNullException.ThrowIfNull(dependentAccounts);
      HasSavedToken = hasSavedToken;
      _dependentAccounts = dependentAccounts;
   }



   /// <summary>
   /// What was typed, without the mask. A token box that shows the mask and is
   /// typed into without being emptied first holds the mask and then the typing;
   /// stored like that, the token would begin with the mask's dots. The mask alone
   /// stays the mask (it means "unchanged"); a box that holds none of it is as it is.
   /// </summary>
   public static string WithoutTheMask(string boxValue)
   {
      ArgumentNullException.ThrowIfNull(boxValue);
      return string.Equals(boxValue, Mask, StringComparison.Ordinal) || !boxValue.Contains(Mask[0], StringComparison.Ordinal)
          ? boxValue
          : boxValue.Replace(Mask[0].ToString(), "", StringComparison.Ordinal);
   }



   /// <summary>True when the vault held a default token when the dialog opened.</summary>
   public bool HasSavedToken { get; }

   /// <summary>How many accounts resolve to the default token.</summary>
   public int DependentCount => _dependentAccounts.Count;

   /// <summary>The initial box value: the mask when a token is saved, empty otherwise.</summary>
   public string InitialBoxValue => HasSavedToken ? Mask : "";

   /// <summary>True when "Remove saved token" applies: a token is saved and the box still shows the mask.</summary>
   public bool CanOfferRemoval(string boxValue) => HasSavedToken && string.Equals(boxValue, Mask, StringComparison.Ordinal);



   /// <summary>What Save will do with <paramref name="boxValue"/>, the box's current content.</summary>
   public SettingsTokenAction Plan(string boxValue)
   {
      ArgumentNullException.ThrowIfNull(boxValue);
      if(string.Equals(boxValue, Mask, StringComparison.Ordinal))
      {
         return HasSavedToken ? SettingsTokenAction.Keep : SettingsTokenAction.Store;
      }
      if(boxValue.Length > 0)
      {
         return SettingsTokenAction.Store;
      }
      return HasSavedToken ? SettingsTokenAction.Remove : SettingsTokenAction.Keep;
   }



   /// <summary>The caption under the box for <paramref name="boxValue"/>; names what Save will do.</summary>
   public string Caption(string boxValue)
   {
      switch(Plan(boxValue))
      {
         case SettingsTokenAction.Store:
            return HasSavedToken
                ? "The default token will be replaced on Save." + RotationNote()
                : "The default token will be saved on Save; Quick Sync uses it on every launch.";
         case SettingsTokenAction.Remove:
            return "The default token will be removed on Save." + RemovalWarning();
         default:
            return HasSavedToken
                ? "A default token is saved" + UsedBy() + ". Type to replace it; clear the box to remove it."
                : "No default token saved. Quick Sync will ask for a token each launch.";
      }
   }



   /// <summary>True when Save would remove a token that accounts depend on; the caption then carries the warning.</summary>
   public bool RemovalBreaksAccounts(string boxValue)
       => Plan(boxValue) == SettingsTokenAction.Remove && DependentCount > 0;



   internal string UsedBy() => DependentCount switch
   {
      0 => " and used by Quick Sync",
      1 => " and used by Quick Sync and 1 account",
      _ => $" and used by Quick Sync and {DependentCount} accounts",
   };



   internal string RotationNote() => DependentCount switch
   {
      0 => "",
      1 => " The 1 account that uses it switches to the new token too.",
      _ => $" The {DependentCount} accounts that use it switch to the new token too.",
   };



   internal string RemovalWarning()
   {
      if(DependentCount == 0)
      {
         return "";
      }
      string names = string.Join(", ", _dependentAccounts);
      return DependentCount == 1
          ? $" 1 account uses it and will stop syncing until a new one is saved: {names}."
          : $" {DependentCount} accounts use it and will stop syncing until a new one is saved: {names}.";
   }
}
