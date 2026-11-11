/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;


namespace gclo.UiTests;


/// <summary>
/// Token-free, deterministic smoke tests against the launched app: the shell and
/// its navigation render, the core dialogs open and close, and offline input does
/// not crash anything. No test talks to GitHub — the app runs on an isolated
/// GCLO_DATA_DIR with no accounts and no stored settings (see <see cref="AppSession"/>).
/// Dialog tests close what they open on the happy path and fall back to a
/// best-effort <see cref="AppSession.TryClickButton"/> in <c>finally</c>, so one
/// failure cannot strand a dialog in front of the next test.
/// </summary>
[Collection(nameof(UiSessionTests))]
public sealed partial class SmokeTests : UiTestBase
{

   public SmokeTests(AppSession session)
       : base(session)
   {
   }



   // (1) The app launches and puts up its shell window.
   [Fact]
   public void MainWindow_AfterLaunch_TitleContainsGclo()
       => Assert.Contains("gclo", Session.MainWindow.Title, StringComparison.Ordinal);



   // (1b) The offline fixture this suite runs against announces itself in the app's
   //      log: a session with it enabled is never mistaken for an ordinary one (#145).
   [Fact]
   public void ActivityLog_AfterLaunch_SaysTheUiTestFixtureIsEnabled()
       => Assert.True(Session.LogContains("UI test fixture enabled"), "the app logs one line when the fixture is honoured");



   // (2) The navigation pane carries the pinned Quick Sync plus both footer commands.
   [Fact]
   public void NavigationPane_AfterLaunch_ShowsQuickSyncAddAccountAndSyncAll()
   {
      Assert.NotNull(Session.WaitForElement("NavQuickSync"));
      Assert.NotNull(Session.WaitForElement("NavAddAccount"));
      Assert.NotNull(Session.WaitForElement("NavSyncAll"));
   }



   // (3) With no accounts and no prior load, the Quick Sync workspace shows the
   //     connect card, and the 'Sync all' footer command has nothing to run.
   [Fact]
   public void ConnectCard_FreshDataDir_ShowsTokenAndLoadAndDisablesSyncAll()
   {
      Assert.NotNull(Session.WaitForElement("ConnectTokenBox"));
      Assert.NotNull(Session.WaitForElement("LoadReposButton"));
      // The fresh data dir must also mean a fresh vault: a default token saved in the
      // developer's real profile must not pre-fill Quick Sync (#60). The session
      // snapshots this at launch, before any test types a token.
      Assert.False(
          Session.TokenPrefilledAtLaunch,
          "Quick Sync was pre-filled with a token from outside the test data directory.");

      AutomationElement syncAll = Session.WaitForElement("NavSyncAll");
      Assert.False(syncAll.IsEnabled, "'Sync all' should be disabled while no accounts exist.");
   }



   // (4) File -> Settings opens the Settings dialog; Cancel closes it.
   [Fact]
   public void FileMenu_Settings_OpensDialogAndCancelCloses()
   {
      Session.InvokeMenuItem("File", "MenuSettings");
      try
      {
         Session.WaitForText("Settings"); // the ContentDialog's title
         Session.ClickButton("Cancel");
         Session.WaitForTextGone("Settings");
      }
      finally
      {
         Session.TryClickButton("Cancel"); // no-op when the happy path closed it
      }
   }



   // (4b) Settings holds a default token per forge (#212): GitHub, and a Gitea
   //      server with its address. A token for a server whose address cannot be
   //      used is not saved: the dialog says why and does not offer Save, and with
   //      the address corrected it does. Nothing is saved here: the dialog is canceled.
   [Fact]
   public void Settings_HasADefaultTokenPerForge_AndDoesNotSaveATokenForAnAddressItRefuses()
   {
      Session.InvokeMenuItem("File", "MenuSettings");
      try
      {
         Session.WaitForText("Default tokens");
         Assert.NotNull(Session.WaitForElement("SettingsDefaultTokenBox")); // GitHub
         Session.EnterText(Session.WaitForElement("SettingsGiteaServerBox"), "http://git.example.test");
         Session.EnterText(Session.WaitForElement("SettingsGiteaTokenBox"), "a-token-that-is-not-saved");

         Session.WaitForText("The server address cannot be used: it has to begin with https://.");
         Session.WaitFor(
             () => Session.FindInApp(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button).And(cf.ByName("Save")).And(cf.ByClassName("Button")))
                   is { IsEnabled: false } save ? save : null,
             "Save to be withheld");

         Session.EnterText(Session.WaitForElement("SettingsGiteaServerBox"), "git.example.test");

         Session.WaitForText("The default token for https://git.example.test/ will be saved on Save; Quick Sync fills it in when it is on that server.");
         Session.WaitFor(
             () => Session.FindInApp(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button).And(cf.ByName("Save")).And(cf.ByClassName("Button")))
                   is { IsEnabled: true } save ? save : null,
             "Save to be offered again");

         Session.ClickButton("Cancel");
         Session.WaitForTextGone("Default tokens");
      }
      finally
      {
         Session.TryClickButton("Cancel"); // no-op when the happy path closed it
      }
   }



   // Help -> Share gclo by email. Under the fixture the invitation is shown in the
   // app instead of being handed to an email program, so the test can read it and
   // nothing opens on the machine that runs the suite.
   [Fact]
   public void HelpMenu_Share_ShowsTheInvitation_WithTheDownloadLink()
   {
      Session.InvokeMenuItem("Help", "MenuShare");
      try
      {
         AutomationElement text = Session.WaitForElement("ShareInvitationText");
         string invitation = text.Patterns.Value.Pattern.Value.Value;

         Assert.StartsWith("Subject: Try gclo", invitation, StringComparison.Ordinal);
         Assert.Contains("https://git.kof22.com/KofTwentyTwo/gclo/releases/latest", invitation, StringComparison.Ordinal);
         Assert.False(Session.App.HasExited);

         Session.ClickButton("Close");
         Session.WaitForElementGone("ShareInvitationText");
      }
      finally
      {
         Session.TryClickButton("Close"); // no-op when the happy path closed it
      }
   }



   // (5) Help -> About shows the semantic version plus the parenthesized git hash.
   [Fact]
   public void HelpMenu_About_ShowsSemanticVersionWithCommitHash()
   {
      Session.InvokeMenuItem("Help", "MenuAbout");
      try
      {
         // The TextBlock exists as soon as the dialog opens; retry until its
         // text has been populated by the AboutDialog constructor.
         AutomationElement version = Session.WaitFor(
             () =>
             {
                AutomationElement? element =
                       Session.FindInApp(cf => cf.ByAutomationId("AboutVersionText"));
                return element is not null
                       && element.Name.StartsWith("Version ", StringComparison.Ordinal)
                           ? element
                           : null;
             },
             "the About dialog's version text");

         string text = version.Name;
         Assert.Matches(VersionPattern(), text);
         Assert.Contains("(", text, StringComparison.Ordinal); // "(<short git hash>)"

         Session.ClickButton("Close");
         Session.WaitForElementGone("AboutVersionText");
      }
      finally
      {
         Session.TryClickButton("Close"); // no-op when the happy path closed it
      }
   }



   // (6) The Add account wizard appears; Next with an empty name keeps it open
   //     (step 1 refuses to advance); Cancel closes it.
   [Fact]
   public void AddAccountWizard_NextWithEmptyName_StaysOpenAndCancelCloses()
   {
      Session.InvokeNavItem("NavAddAccount");
      try
      {
         Session.WaitForElement("WizardNameBox"); // wizard step 1 is on screen
         Session.WaitForText("Step 1 of 4: Identity"); // right dialog, right step

         Session.ClickButton("Next");

         // Positive oracle: the step-1 validation message appearing proves the
         // Next handler ran and rejected; only then is "still on step 1" meaningful.
         Session.WaitForText("Enter a name for this account.");
         Assert.NotNull(Session.FindInApp(cf => cf.ByName("Step 1 of 4: Identity")));
         Assert.NotNull(Session.WaitForElement("WizardNameBox")); // still open
         Assert.False(Session.App.HasExited);

         Session.ClickButton("Cancel");
         Session.WaitForElementGone("WizardNameBox");
      }
      finally
      {
         Session.TryClickButton("Cancel"); // no-op when the happy path closed it
      }
   }



   // (7) Typing into the token box does not crash the app, and the org combo
   //     stays present. The text is too short to be taken for a token, so nothing
   //     is looked up: these tests run without network access or credentials, and
   //     a longer made-up token would be sent to the real API to be refused. The
   //     listing itself is exercised offline, with the fixture token, in RepoTableTests.
   [Fact]
   public void ConnectTokenBox_TypingText_KeepsAppAliveAndOrgBoxPresent()
   {
      // The box takes a token once a place was chosen (#209).
      Session.WaitForElement("ConnectPlaceGitHub").AsRadioButton().IsChecked = true;
      AutomationElement tokenBox = Session.WaitFor(
          () => Session.FindInApp(cf => cf.ByAutomationId("ConnectTokenBox")) is { IsEnabled: true } box ? box : null,
          "the token box to take a token once a place is chosen");
      Session.EnterText(tokenBox, "ghp_short");

      Assert.NotNull(Session.WaitForElement("ConnectOrgBox"));
      Assert.False(Session.App.HasExited);
   }



   // (8) Regression for the dialog-collision crash: WinUI allows one open
   //     ContentDialog per XamlRoot, and a second ShowAsync throws a COMException
   //     that — escaping an async void handler — used to kill the whole process
   //     (0xc000027b). Commanding 'Add account' while About is open must now be
   //     ignored: the app stays alive, About stays up, and no wizard appears.
   [Fact]
   public void AddAccountWhileAboutDialogOpen_IsIgnoredAndAppSurvives()
   {
      Session.InvokeMenuItem("Help", "MenuAbout");
      try
      {
         Session.WaitForElement("AboutVersionText");

         Session.InvokeNavItem("NavAddAccount"); // second dialog: must be a no-op

         // Positive oracle: the shell logs that it declined to open the wizard, so
         // the negative assertions below are checked after the command was handled,
         // not after an arbitrary pause.
         Session.WaitForLogLine("Account wizard not opened: another dialog is open.");

         Assert.False(Session.App.HasExited);
         Assert.Null(Session.FindInApp(cf => cf.ByAutomationId("WizardNameBox")));
         Assert.NotNull(Session.FindInApp(cf => cf.ByAutomationId("AboutVersionText")));

         Session.ClickButton("Close");
         Session.WaitForElementGone("AboutVersionText");
      }
      finally
      {
         Session.TryClickButton("Close"); // no-op when the happy path closed it
      }
   }



   // (9) A new account on a Gitea server (#206). Step 1 asks for the server's
   //     address only once that place is chosen; step 2 names the server the token
   //     is checked with; and step 3 lists what that server listed, which here is
   //     the fixture's organization for a Gitea server: the fixture token is
   //     answered offline for the place the wizard asked, so nothing leaves the
   //     machine. The wizard is canceled on step 3, so nothing is saved either.
   [Fact]
   public void AddAccountWizard_OnAGiteaServer_ChecksTheTokenWithThatServer()
   {
      Session.InvokeNavItem("NavAddAccount");
      try
      {
         Session.EnterText(Session.WaitForElement("WizardNameBox"), "Forge");
         Assert.Null(Session.FindInApp(cf => cf.ByAutomationId("WizardServerBox"))); // GitHub is the default: no address is asked

         Session.WaitForElement("WizardPlaceGitea").AsRadioButton().IsChecked = true;
         Session.EnterText(Session.WaitForElement("WizardServerBox"), "git.example.test");
         Session.ClickButton("Next");

         Session.WaitForText("Step 2 of 4: Access token");
         Session.WaitForText("Next checks the token with https://git.example.test/ and lists the organizations it can see.");
         Session.EnterText(Session.WaitForElement("WizardTokenBox"), "gclo-uitest-fixture-token");
         Session.ClickButton("Next");

         Session.WaitForText("Step 3 of 4: Organization", TimeSpan.FromSeconds(20));
         ComboBox orgBox = Session.WaitForElement("WizardOrgBox").AsComboBox();
         Session.WaitFor(
             () =>
             {
                orgBox.Expand();
                return orgBox.Items.FirstOrDefault(i => string.Equals(i.Name, "fixture-gitea-org", StringComparison.Ordinal));
             },
             "the fixture's Gitea organization in the organization list");
         orgBox.Collapse();
         Assert.False(Session.App.HasExited);

         Session.ClickButton("Cancel");
         Session.WaitForElementGone("WizardOrgBox");
      }
      finally
      {
         Session.TryClickButton("Cancel"); // no-op when the happy path closed it
      }
   }



   // (10) Quick Sync on a Gitea server (#209). The card asks where the repositories
   //      are; with Gitea chosen and an address entered its title names the server.
   //      Entering the token sends nothing: the organizations are listed when Find
   //      is pressed, and they are the ones of that server (the fixture answers for
   //      the place that was asked, offline). Nothing is loaded, and the card is put
   //      back on GitHub with an empty token box, so the other tests of this
   //      session find it as they expect.
   [Fact]
   public void QuickSync_OnAGiteaServer_ListsThatServersOrganizations_WhenAsked()
   {
      try
      {
         Session.WaitForElement("ConnectPlaceGitea").AsRadioButton().IsChecked = true;
         Session.EnterText(Session.WaitForElement("ConnectServerBox"), "git.example.test");
         AutomationElement tokenBox = Session.WaitFor(
             () => Session.FindInApp(cf => cf.ByAutomationId("ConnectTokenBox")) is { IsEnabled: true } box ? box : null,
             "the token box to take a token once a place is chosen");
         Session.EnterText(tokenBox, "gclo-uitest-fixture-token");

         // The address was taken with the token, as it was normalized, and the title names the server.
         Session.WaitForText("Connect to git.example.test");

         Session.WaitFor(
             () => Session.FindInApp(cf => cf.ByAutomationId("ConnectFindOrgsButton")) is { IsEnabled: true } button ? button : null,
             "the Find button to enable");
         Session.ClickButton("Find");
         ComboBox orgBox = Session.WaitForElement("ConnectOrgBox").AsComboBox();
         Session.WaitFor(
             () =>
             {
                orgBox.Expand();
                return orgBox.Items.FirstOrDefault(i => string.Equals(i.Name, "fixture-gitea-org", StringComparison.Ordinal));
             },
             "the fixture's Gitea organization in the organization list");
         orgBox.Collapse();

         // The address typed over names no place, so Find is withheld; typed back, without
         // the box being left, it is the server again and Find is offered.
         AutomationElement serverBox = Session.WaitForElement("ConnectServerBox");
         Session.EnterText(serverBox, "https://git.example.test/x");
         Session.WaitFor(
             () => Session.FindInApp(cf => cf.ByAutomationId("ConnectFindOrgsButton")) is { IsEnabled: false } button ? button : null,
             "Find to be withheld while the address is typed over");
         Session.EnterText(serverBox, "https://git.example.test/");
         Session.WaitFor(
             () => Session.FindInApp(cf => cf.ByAutomationId("ConnectFindOrgsButton")) is { IsEnabled: true } button ? button : null,
             "Find to be offered again for the address that was typed back");
         Assert.False(Session.App.HasExited);
      }
      finally
      {
         // Back to GitHub: choosing another place empties the token and the organization list.
         Session.WaitForElement("ConnectPlaceGitHub").AsRadioButton().IsChecked = true;
         Session.WaitForText("Connect to GitHub");
      }
   }



   // (11) The button at the top of the pane closes the pane to its icons and opens
   //      it again. It used to do nothing: the pane was set to stay open. The button
   //      names what it does next, in the language of Windows, so the oracle is that
   //      the name changes and changes back.
   [Fact]
   public void PaneButton_ClosesThePane_AndOpensItAgain()
   {
      string whenOpen = Session.WaitForElement("TogglePaneButton").Name;
      try
      {
         Session.InvokeNavItem("TogglePaneButton");
         Session.WaitFor(
             () => Session.FindInApp(cf => cf.ByAutomationId("TogglePaneButton")) is { } button
                   && !string.Equals(button.Name, whenOpen, StringComparison.Ordinal) ? button : null,
             "the pane to close");
         Assert.NotNull(Session.WaitForElement("NavQuickSync")); // still there, as its icon
      }
      finally
      {
         if(!string.Equals(Session.WaitForElement("TogglePaneButton").Name, whenOpen, StringComparison.Ordinal))
         {
            Session.InvokeNavItem("TogglePaneButton");
         }
         Session.WaitFor(
             () => Session.FindInApp(cf => cf.ByAutomationId("TogglePaneButton")) is { } button
                   && string.Equals(button.Name, whenOpen, StringComparison.Ordinal) ? button : null,
             "the pane to open again");
      }
   }



   // (12) Resizing below the minimum window size is intentionally not covered:
   //     driving OS-level resize through UIA transforms is flaky across DPI
   //     scales, and the minimum is already enforced by OverlappedPresenter.

   [GeneratedRegex(@"^Version \d+\.\d+\.\d+", RegexOptions.None, 1000)]
   private static partial Regex VersionPattern();



   // (12) One window per data folder (#216). gclo started again on the data folder
   //      of the open window opens no second window: the process ends by itself,
   //      and the window that is open comes out of the taskbar. The second process
   //      is the one this test starts, and the only one it ever ends.
   [Fact]
   public void StartingGcloAgain_OnTheSameDataFolder_EndsAtOnce_AndBringsTheOpenWindowBack()
   {
      FlaUI.Core.Patterns.IWindowPattern window = Session.MainWindow.Patterns.Window.Pattern;
      window.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Minimized);
      Session.WaitFor(
          () => window.WindowVisualState.Value == FlaUI.Core.Definitions.WindowVisualState.Minimized ? Session.MainWindow : null,
          "the window to be minimized");

      var startInfo = new System.Diagnostics.ProcessStartInfo(Session.ExePath)
      {
         WorkingDirectory = Path.GetDirectoryName(Session.ExePath) ?? "",
      };
      startInfo.Environment["GCLO_DATA_DIR"] = Session.DataDirectory;
      startInfo.Environment["GCLO_UITEST_FIXTURE"] = "1";
      using System.Diagnostics.Process second = System.Diagnostics.Process.Start(startInfo)!;
      try
      {
         Assert.True(second.WaitForExit(TimeSpan.FromSeconds(30)), "the second gclo did not end by itself");
         Assert.Equal(0, second.ExitCode);

         Session.WaitFor(
             () => window.WindowVisualState.Value != FlaUI.Core.Definitions.WindowVisualState.Minimized ? Session.MainWindow : null,
             "the open window to come back from the taskbar");
         Assert.False(Session.App.HasExited);
      }
      finally
      {
         if(!second.HasExited)
         {
            second.Kill();
         }
      }
   }
}
