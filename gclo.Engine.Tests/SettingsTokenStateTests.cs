/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.ViewModels;


namespace gclo.Engine.Tests;


/// <summary>
/// <see cref="SettingsTokenState"/>: the mask stands for a saved token, Save's action
/// follows from what the box holds, and the captions name the consequence (#101).
/// </summary>
public sealed class SettingsTokenStateTests
{
   private static SettingsTokenState Saved(params string[] dependents) => new(hasSavedToken: true, dependents);

   private static SettingsTokenState Empty() => new(hasSavedToken: false, []);



   [Fact]
   public void NullDependents_Throws()
       => Assert.Throws<ArgumentNullException>(() => new SettingsTokenState(true, null!));



   [Fact]
   public void InitialBoxValue_IsTheMaskOnlyWhenATokenIsSaved()
   {
      Assert.Equal(SettingsTokenState.Mask, Saved().InitialBoxValue);
      Assert.Equal("", Empty().InitialBoxValue);
      Assert.DoesNotContain("ghp", SettingsTokenState.Mask);
   }



   [Theory]
   [InlineData(true, SettingsTokenState.Mask, SettingsTokenAction.Keep)]
   [InlineData(true, "ghp_new", SettingsTokenAction.Store)]
   [InlineData(true, "", SettingsTokenAction.Remove)]
   [InlineData(false, "", SettingsTokenAction.Keep)]
   [InlineData(false, "ghp_new", SettingsTokenAction.Store)]
   [InlineData(false, SettingsTokenState.Mask, SettingsTokenAction.Store)]
   public void Plan_FollowsFromTheBox(bool hasSaved, string box, SettingsTokenAction expected)
   {
      var state = new SettingsTokenState(hasSaved, []);

      Assert.Equal(expected, state.Plan(box));
   }



   [Fact]
   public void Plan_NullBox_Throws()
       => Assert.Throws<ArgumentNullException>(() => Saved().Plan(null!));



   [Fact]
   public void CanOfferRemoval_OnlyWhileTheMaskIsUntouched()
   {
      Assert.True(Saved().CanOfferRemoval(SettingsTokenState.Mask));
      Assert.False(Saved().CanOfferRemoval("ghp_new"));
      Assert.False(Saved().CanOfferRemoval(""));
      Assert.False(Empty().CanOfferRemoval(SettingsTokenState.Mask));
   }



   [Fact]
   public void Caption_SavedAndUntouched_SaysWhoUsesIt()
   {
      Assert.Equal(
          "A default token is saved and used by Quick Sync. Type to replace it; clear the box to remove it.",
          Saved().Caption(SettingsTokenState.Mask));
      Assert.Contains("Quick Sync and 1 account.", Saved("Work").Caption(SettingsTokenState.Mask));
      Assert.Contains("Quick Sync and 2 accounts.", Saved("Work", "Home").Caption(SettingsTokenState.Mask));
   }



   [Fact]
   public void Caption_NothingSaved_SaysSo()
   {
      Assert.StartsWith("No default token saved.", Empty().Caption(""), StringComparison.Ordinal);
      Assert.StartsWith("The default token will be saved on Save", Empty().Caption("ghp_new"), StringComparison.Ordinal);
   }



   [Fact]
   public void Caption_Replacing_MentionsTheAccountsThatRotateWithIt()
   {
      Assert.Equal("The default token will be replaced on Save.", Saved().Caption("ghp_new"));
      Assert.Contains("The 1 account that uses it switches", Saved("Work").Caption("ghp_new"));
      Assert.Contains("The 2 accounts that use it switch", Saved("Work", "Home").Caption("ghp_new"));
   }



   [Fact]
   public void Caption_Removing_WarnsAndNamesTheDependentAccounts()
   {
      Assert.Equal("The default token will be removed on Save.", Saved().Caption(""));
      Assert.False(Saved().RemovalBreaksAccounts(""));

      string one = Saved("Work").Caption("");
      Assert.Contains("1 account uses it and will stop syncing", one);
      Assert.Contains("Work", one);
      Assert.True(Saved("Work").RemovalBreaksAccounts(""));

      string two = Saved("Work", "Home").Caption("");
      Assert.Contains("2 accounts use it and will stop syncing", two);
      Assert.Contains("Work, Home", two);
   }



   [Fact]
   public void RemovalBreaksAccounts_IsFalseUnlessSaveWouldRemove()
   {
      Assert.False(Saved("Work").RemovalBreaksAccounts(SettingsTokenState.Mask));
      Assert.False(Saved("Work").RemovalBreaksAccounts("ghp_new"));
      Assert.False(Empty().RemovalBreaksAccounts(""));
   }



   [Fact]
   public void Captions_NeverContainTheMaskOrATokenValue()
   {
      foreach(string box in new[] { SettingsTokenState.Mask, "ghp_secret", "" })
      {
         string caption = Saved("Work").Caption(box);
         Assert.DoesNotContain("ghp_secret", caption);
         Assert.DoesNotContain(SettingsTokenState.Mask, caption);
      }
   }
}
