using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace gclo.UiTests;

/// <summary>
/// Drives the repository table (WinUI.TableView, #33) against the app's offline
/// fixture: the magic token routes Quick Sync to a fixed organization with three
/// repositories (see <c>UiTestFixture</c> in the app), so the table renders without
/// GitHub. Its own collection, because loading repositories replaces the connect
/// card the smoke tests expect to find.
/// </summary>
[Collection(nameof(UiTableTests))]
public sealed class RepoTableTests : UiTestBase
{
    private const string FixtureToken = "gclo-uitest-fixture-token";

    public RepoTableTests(AppSession session)
        : base(session)
    {
    }

    // (1) The fixture organization loads into the table: the TableView is reachable
    //     by its automation id, the three repositories are rendered as cells, and
    //     the header row (select-all, funnels) is in the UI Automation tree.
    [Fact]
    public void LoadRepositories_WithFixtureToken_RendersTheTable()
    {
        EnsureLoaded();

        Assert.NotNull(Session.FindInApp(cf => cf.ByName("alpha")));
        Assert.NotNull(Session.FindInApp(cf => cf.ByName("bravo")));
        Assert.NotNull(Session.FindInApp(cf => cf.ByName("charlie")));
        Assert.NotNull(Session.WaitFor(() => Funnel("StatusFilterButton"), "the Status funnel"));
        Assert.NotNull(Session.WaitFor(() => ColumnHeader("Select"), "the select-all header"));
    }

    // (2) Column headers carry composed sort names and the Invoke pattern; invoking
    //     one sorts through the view model (ascending, then descending, then back to
    //     ascending on the control's "clear" step), and other columns stay unsorted.
    [Fact]
    public void Headers_InvokeSortsThroughTheViewModel()
    {
        EnsureLoaded();

        AutomationElement header = Session.WaitFor(() => ColumnHeader("Branch, not sorted"), "the Branch column header");
        Assert.True(header.Patterns.Invoke.IsSupported, "a sortable header exposes the Invoke pattern");

        header.Patterns.Invoke.Pattern.Invoke();
        Session.WaitFor(() => ColumnHeader("Branch, sorted ascending"), "the header after one sort");
        Assert.NotNull(ColumnHeader("Name, not sorted"));

        header.Patterns.Invoke.Pattern.Invoke();
        Session.WaitFor(() => ColumnHeader("Branch, sorted descending"), "the header after a second sort");

        header.Patterns.Invoke.Pattern.Invoke(); // the control's third step is "clear"; the view model starts over
        Session.WaitFor(() => ColumnHeader("Branch, sorted ascending"), "the header after a third sort");
    }

    // (3) The per-column funnel opens its flyout; its text box drives the view
    //     model's filter (rows vanish, the empty-filter text appears, the funnel
    //     names the active filter) and Clear restores every row.
    [Fact]
    public void NameFunnel_FiltersRows_AndClearRestoresThem()
    {
        EnsureLoaded();

        try
        {
            Session.WaitFor(() => Funnel("NameFilterButton"), "the Name funnel").AsButton().Invoke();
            AutomationElement box = Session.WaitForElement("NameFilterBox");
            Session.EnterText(box, "zzz");

            Session.WaitFor(
                () => Session.FindInApp(cf => cf.ByName("Filter by name, filtering on 'zzz'")),
                "the funnel to report the active filter");
            Session.WaitForText("No repositories match this filter.");
            AppSession.WaitUntilGone(() => Session.FindInApp(cf => cf.ByName("alpha")), "the filtered-out row");

            Session.ClickButton("Clear");
            Session.WaitFor(() => Session.FindInApp(cf => cf.ByName("Filter by name")), "the funnel after Clear");
            Session.WaitFor(() => Session.FindInApp(cf => cf.ByName("alpha")), "the row to come back");
            Session.WaitForTextGone("No repositories match this filter.");
        }
        finally
        {
            Keyboard.Press(VirtualKeyShort.ESCAPE); // close the flyout if it is still open
        }
    }

    // (4) Keyboard reachability of the funnels (Tab from the select-all checkbox) is
    //     not asserted here: synthesized Tab keystrokes did not move focus inside the
    //     TableView header under FlaUI on the development machine, so that remains a
    //     manual Narrator/keyboard check (see the #33 acceptance notes).

    /// <summary>A column header by its automation name; the header row is in the UIA tree thanks to AccessibleTableView.</summary>
    private AutomationElement? ColumnHeader(string name)
        => Session.FindInApp(cf => cf.ByAutomationId("RepoListView"))?.FindFirstDescendant(cf => cf.ByName(name));

    /// <summary>The funnel button carrying <paramref name="automationId"/>, inside its column header.</summary>
    private AutomationElement? Funnel(string automationId)
        => Session.FindInApp(cf => cf.ByAutomationId("RepoListView"))?.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

    /// <summary>
    /// Loads the fixture organization once per session: paste the magic token (the
    /// org lookup is answered by the fixture), pick the org, then Load repositories.
    /// A session that already shows the table skips straight through.
    /// </summary>
    private void EnsureLoaded()
    {
        if (Session.FindInApp(cf => cf.ByAutomationId("RepoListView")) is not null)
        {
            return;
        }

        AutomationElement tokenBox = Session.WaitForElement("ConnectTokenBox");
        Session.EnterText(tokenBox, FixtureToken);
        // The debounced org lookup is answered by the fixture (one organization); the
        // log line is the oracle, then the org is picked from the combo box's list so
        // the selection commits to the view model's Organization, which enables Load.
        Session.WaitForLogLine("Organization lookup finished: 1 organizations", TimeSpan.FromSeconds(20));
        ComboBox orgBox = Session.WaitForElement("ConnectOrgBox").AsComboBox();
        Session.WaitFor(
            () =>
            {
                orgBox.Expand();
                return orgBox.Items.FirstOrDefault(i => i.Name == "fixture-org");
            },
            "the fixture org in the organization list");
        orgBox.Select("fixture-org");
        orgBox.Collapse();

        Session.WaitFor(
            () => Session.FindInApp(cf => cf.ByName("Load repositories")) is { IsEnabled: true } button ? button : null,
            "the Load repositories button to enable");
        Session.ClickButton("Load repositories");
        Session.WaitForElement("RepoListView", TimeSpan.FromSeconds(20));
        Session.WaitFor(() => Session.FindInApp(cf => cf.ByName("charlie")), "the last fixture row");
    }
}

/// <summary>
/// A second collection (and therefore a second launched app) for the table tests;
/// see <see cref="RepoTableTests"/> for why they cannot share the smoke session.
/// </summary>
[CollectionDefinition(nameof(UiTableTests), DisableParallelization = true)]
public sealed class UiTableTests : ICollectionFixture<AppSession>
{
}
