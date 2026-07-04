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
    /// <summary>Small pause after a button invoke whose handler runs behind a dialog deferral.</summary>
    private static readonly TimeSpan PostInvokeSettle = TimeSpan.FromMilliseconds(300);

    public SmokeTests(AppSession session)
        : base(session)
    {
    }

    // (1) The app launches and puts up its shell window.
    [Fact]
    public void MainWindow_AfterLaunch_TitleContainsGclo()
        => Assert.Contains("gclo", Session.MainWindow.Title, StringComparison.Ordinal);

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
            Thread.Sleep(PostInvokeSettle); // validation runs behind the dialog deferral

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

    // (7) Typing into the token box (which kicks the offline org-listing path) does
    //     not crash the app, and the org combo stays present. No token validation
    //     is asserted — these tests run without network access or credentials.
    [Fact]
    public void ConnectTokenBox_TypingText_KeepsAppAliveAndOrgBoxPresent()
    {
        AutomationElement tokenBox = Session.WaitForElement("ConnectTokenBox");
        Session.EnterText(tokenBox, "ghp_uitests_offline_dummy_token");

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
            Thread.Sleep(PostInvokeSettle);

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

    // (9) Resizing below the minimum window size is intentionally not covered:
    //     driving OS-level resize through UIA transforms is flaky across DPI
    //     scales, and the minimum is already enforced by OverlappedPresenter.

    [GeneratedRegex(@"^Version \d+\.\d+\.\d+")]
    private static partial Regex VersionPattern();
}
