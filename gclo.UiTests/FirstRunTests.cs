/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using FlaUI.Core.AutomationElements;


namespace gclo.UiTests;


/// <summary>
/// What the app shows the very first time it runs (#209), in a launched app of its
/// own: the other collections choose a place on Quick Sync's card as they go, and
/// this one has to see the card before anybody did.
/// </summary>
[Collection(nameof(UiFirstRunTests))]
public sealed class FirstRunTests : UiTestBase
{

   public FirstRunTests(AppSession session)
       : base(session)
   {
   }



   // On a first run no place is chosen for the user, and the token box takes no
   // token. Keyboard focus is not a choice: the window opens with the focus on the
   // first control of the card, and a radio group that selected what has the focus
   // had GitHub chosen before the user did anything. Choosing is an act.
   [Fact]
   public void OnAFirstRun_NoPlaceIsChosen_AndFocusAloneChoosesNone()
   {
      Assert.Equal("Connect", Session.WaitForElement("ConnectTitle").Name);
      AutomationElement gitHub = Session.WaitForElement("ConnectPlaceGitHub");
      AutomationElement gitea = Session.WaitForElement("ConnectPlaceGitea");
      Assert.False(gitHub.AsRadioButton().IsChecked);
      Assert.False(gitea.AsRadioButton().IsChecked);
      Assert.False(Session.WaitForElement("ConnectTokenBox").IsEnabled);

      foreach(AutomationElement focused in (AutomationElement[])[gitea, gitHub])
      {
         focused.Focus();
         Session.WaitFor(() => focused.Properties.HasKeyboardFocus.ValueOrDefault ? focused : null, "the keyboard focus on a place");

         Assert.False(gitHub.AsRadioButton().IsChecked);
         Assert.False(gitea.AsRadioButton().IsChecked);
         Assert.Equal("Connect", Session.WaitForElement("ConnectTitle").Name);
         Assert.False(Session.WaitForElement("ConnectTokenBox").IsEnabled);
      }

      gitea.AsRadioButton().IsChecked = true;

      Session.WaitForText("Connect to Gitea");
      Assert.True(gitea.AsRadioButton().IsChecked);
      Assert.False(gitHub.AsRadioButton().IsChecked);
      Session.WaitFor(
          () => Session.FindInApp(cf => cf.ByAutomationId("ConnectTokenBox")) is { IsEnabled: true } box ? box : null,
          "the token box to take a token once a place is chosen");
      Assert.False(Session.LogContains("Organization lookup started"));
   }
}



/// <summary>
/// A third collection (and therefore a third launched app), whose Quick Sync card
/// nobody touched; see <see cref="FirstRunTests"/>.
/// </summary>
[CollectionDefinition(nameof(UiFirstRunTests), DisableParallelization = true)]
public sealed class UiFirstRunTests : ICollectionFixture<AppSession>
{
}
