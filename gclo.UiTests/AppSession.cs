using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace gclo.UiTests;

/// <summary>
/// One launched gclo.exe plus the UIA3 automation that drives it, shared by every
/// UI test in the collection (see <see cref="UiSessionTests"/>).
///
/// The exe comes from the GCLO_UITEST_EXE environment variable when set, otherwise
/// from the repo-relative Debug/x64 output (found by walking up from the test's
/// base directory to the folder containing gclo.slnx). Each session gets a unique
/// temp directory passed to the app as GCLO_DATA_DIR, so tests always start with
/// no settings, no accounts, and no logs — and never touch the real user profile.
///
/// Dispose closes the app (kill as fallback), disposes the automation only after
/// the app is gone, and best-effort deletes the temp data directory.
/// </summary>
public sealed class AppSession : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Launches gclo against a fresh temp GCLO_DATA_DIR and waits for its main window.</summary>
    public AppSession()
    {
        ExePath = ResolveExePath();
        DataDirectory = Path.Combine(Path.GetTempPath(), "gclo-uitests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);

        var startInfo = new ProcessStartInfo(ExePath)
        {
            WorkingDirectory = Path.GetDirectoryName(ExePath) ?? "",
        };
        startInfo.Environment["GCLO_DATA_DIR"] = DataDirectory;

        Automation = new UIA3Automation();
        try
        {
            App = Application.Launch(startInfo);
            MainWindow = WaitForMainWindow();
        }
        catch
        {
            // A half-launched session must not leak the process, the automation,
            // or the temp directory; xunit will surface the original exception.
            Dispose();
            throw;
        }
    }

    /// <summary>The gclo.exe under test.</summary>
    public string ExePath { get; }

    /// <summary>The temp directory the app sees as its data root (via GCLO_DATA_DIR).</summary>
    public string DataDirectory { get; }

    /// <summary>The UIA3 automation used for every lookup and pattern call.</summary>
    public UIA3Automation Automation { get; }

    /// <summary>The launched application (never null after construction).</summary>
    public Application App { get; }

    /// <summary>The app's main window, resolved once at launch.</summary>
    public Window MainWindow { get; }

    // ---------------------------------------------------------------- lookups

    /// <summary>
    /// Finds the first element matching <paramref name="condition"/> in the main
    /// window — or in any other top-level window of the app's process. The second
    /// leg matters because WinAppSDK menu flyouts open in windowed popups (separate
    /// top-level HWNDs), so their items are not descendants of the main window.
    /// </summary>
    public AutomationElement? FindInApp(Func<ConditionFactory, ConditionBase> condition)
    {
        AutomationElement? found = MainWindow.FindFirstDescendant(condition);
        if (found is not null)
        {
            return found;
        }

        foreach (AutomationElement topLevel in Automation.GetDesktop()
            .FindAllChildren(cf => cf.ByProcessId(App.ProcessId)))
        {
            if (topLevel.Equals(MainWindow))
            {
                continue;
            }
            found = topLevel.FindFirstDescendant(condition);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>
    /// Retries <paramref name="lookup"/> until it returns an element, throwing a
    /// <see cref="TimeoutException"/> naming <paramref name="description"/> when the
    /// timeout (default 10s) elapses first. Exceptions inside the lookup (stale
    /// elements mid-transition) count as "not found yet" and are retried.
    /// </summary>
    public AutomationElement WaitFor(
        Func<AutomationElement?> lookup, string description, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        TimeSpan effective = timeout ?? DefaultTimeout;
        RetryResult<AutomationElement?> result = Retry.WhileNull(
            lookup, timeout: effective, interval: PollInterval, ignoreException: true);
        if (result.Result is { } element)
        {
            return element;
        }
        throw new TimeoutException(
            $"Timed out after {effective.TotalSeconds:0}s waiting for {description}. "
            + $"(app exited: {App.HasExited})");
    }

    /// <summary>
    /// Retries until <paramref name="lookup"/> returns null (the element left the
    /// tree), throwing a <see cref="TimeoutException"/> naming
    /// <paramref name="description"/> when it is still present at the timeout.
    /// </summary>
    public static void WaitUntilGone(
        Func<AutomationElement?> lookup, string description, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        TimeSpan effective = timeout ?? DefaultTimeout;
        RetryResult<bool> result = Retry.WhileTrue(
            () => lookup() is not null, timeout: effective, interval: PollInterval, ignoreException: true);
        if (!result.Success)
        {
            throw new TimeoutException(
                $"Timed out after {effective.TotalSeconds:0}s waiting for {description} to go away.");
        }
    }

    /// <summary>Waits for the element carrying <paramref name="automationId"/>.</summary>
    public AutomationElement WaitForElement(string automationId, TimeSpan? timeout = null)
        => WaitFor(
            () => FindInApp(cf => cf.ByAutomationId(automationId)),
            $"element '{automationId}'",
            timeout);

    /// <summary>Waits until no element carries <paramref name="automationId"/> anymore.</summary>
    public void WaitForElementGone(string automationId, TimeSpan? timeout = null)
        => WaitUntilGone(
            () => FindInApp(cf => cf.ByAutomationId(automationId)),
            $"element '{automationId}'",
            timeout);

    /// <summary>Waits for a text element whose name is exactly <paramref name="text"/>.</summary>
    public AutomationElement WaitForText(string text, TimeSpan? timeout = null)
        => WaitFor(
            () => FindInApp(cf => cf.ByControlType(ControlType.Text).And(cf.ByName(text))),
            $"text '{text}'",
            timeout);

    /// <summary>Waits until no text element named <paramref name="text"/> remains.</summary>
    public void WaitForTextGone(string text, TimeSpan? timeout = null)
        => WaitUntilGone(
            () => FindInApp(cf => cf.ByControlType(ControlType.Text).And(cf.ByName(text))),
            $"text '{text}'",
            timeout);

    // ---------------------------------------------------------------- actions

    /// <summary>
    /// Opens the menu bar item named <paramref name="menuBarItemName"/> (e.g. "File")
    /// and invokes the flyout item carrying <paramref name="itemAutomationId"/>.
    /// The bar item is opened via its ExpandCollapse pattern (WinUI's MenuBarItem
    /// exposes it) with Invoke as fallback; the flyout item is then looked up across
    /// the app's windows because menu popups are windowed in WinAppSDK.
    /// </summary>
    public void InvokeMenuItem(string menuBarItemName, string itemAutomationId)
    {
        AutomationElement barItem = WaitFor(
            () => MainWindow.FindFirstDescendant(cf =>
                    cf.ByControlType(ControlType.MenuItem).And(cf.ByName(menuBarItemName)))
                ?? MainWindow.FindFirstDescendant(cf => cf.ByName(menuBarItemName)),
            $"menu bar item '{menuBarItemName}'");

        if (barItem.Patterns.ExpandCollapse.TryGetPattern(out var expandCollapse))
        {
            expandCollapse.Expand();
            Wait.UntilInputIsProcessed();
        }
        else
        {
            InvokeElement(barItem);
        }

        AutomationElement item = WaitFor(
            () => FindInApp(cf => cf.ByAutomationId(itemAutomationId)),
            $"menu item '{itemAutomationId}' under '{menuBarItemName}'");
        InvokeElement(item);
    }

    /// <summary>
    /// Activates a NavigationView item by AutomationId. NavigationViewItem exposes
    /// Invoke (preferred, it routes through the pane's selection logic) and
    /// SelectionItem; a raw click is the last resort.
    /// </summary>
    public void InvokeNavItem(string automationId)
        => InvokeElement(WaitForElement(automationId));

    /// <summary>
    /// Invokes the XAML button named <paramref name="name"/>. The ClassName filter
    /// ("Button") keeps ContentDialog buttons (real XAML buttons named by their
    /// *ButtonText) from colliding with the window's non-client caption buttons,
    /// whose UIA names ("Close", ...) overlap dialog button captions.
    /// </summary>
    public void ClickButton(string name, TimeSpan? timeout = null)
    {
        AutomationElement button = WaitFor(
            () => FindInApp(cf => cf.ByControlType(ControlType.Button)
                .And(cf.ByName(name))
                .And(cf.ByClassName("Button"))),
            $"button '{name}'",
            timeout);
        InvokeElement(button);
    }

    /// <summary>
    /// Best-effort variant of <see cref="ClickButton"/> for <c>finally</c> cleanup:
    /// clicks the button when it is currently present and swallows every failure,
    /// so closing a leftover dialog can never mask the test's own exception.
    /// </summary>
    public void TryClickButton(string name)
    {
        try
        {
            AutomationElement? button = FindInApp(cf => cf.ByControlType(ControlType.Button)
                .And(cf.ByName(name))
                .And(cf.ByClassName("Button")));
            if (button is not null)
            {
                InvokeElement(button);
            }
        }
        catch
        {
            // Cleanup only — the test outcome was decided in the try body.
        }
    }

    /// <summary>
    /// Writes <paramref name="text"/> into <paramref name="element"/>: the UIA Value
    /// pattern when available and writable (works without window focus), otherwise
    /// focus plus real keystrokes via <see cref="Keyboard"/>.
    /// </summary>
    public void EnterText(AutomationElement element, string text)
    {
        ArgumentNullException.ThrowIfNull(element);
        try
        {
            if (element.Patterns.Value.TryGetPattern(out var value)
                && !value.IsReadOnly.ValueOrDefault)
            {
                value.SetValue(text);
                return;
            }
        }
        catch
        {
            // Some boxes reject SetValue (or the element re-rendered); type instead.
        }

        MainWindow.Focus();
        element.Focus();
        Keyboard.Type(text);
        Wait.UntilInputIsProcessed();
    }

    /// <summary>Invoke pattern first, SelectionItem second, mouse click as last resort.</summary>
    private static void InvokeElement(AutomationElement element)
    {
        if (element.Patterns.Invoke.TryGetPattern(out var invoke))
        {
            invoke.Invoke();
        }
        else if (element.Patterns.SelectionItem.TryGetPattern(out var selectionItem))
        {
            selectionItem.Select();
        }
        else
        {
            element.Click();
        }
        Wait.UntilInputIsProcessed();
    }

    // ---------------------------------------------------------------- lifecycle

    private Window WaitForMainWindow()
    {
        RetryResult<Window?> result = Retry.WhileNull(
            () =>
            {
                if (App.HasExited)
                {
                    throw new InvalidOperationException(
                        $"gclo exited while waiting for its main window (exit code {App.ExitCode}). "
                        + $"Exe: {ExePath}");
                }
                try
                {
                    return App.GetMainWindow(Automation, TimeSpan.FromMilliseconds(500));
                }
                catch
                {
                    return null; // not ready yet; retried until the outer timeout
                }
            },
            timeout: LaunchTimeout,
            interval: PollInterval);

        if (result.Result is { } window)
        {
            return window;
        }
        throw new TimeoutException(
            $"gclo's main window did not appear within {LaunchTimeout.TotalSeconds:0}s. Exe: {ExePath}");
    }

    /// <summary>
    /// The exe to drive: GCLO_UITEST_EXE when set, else the repo's Debug/x64 output,
    /// located by walking up from the test assembly to the folder holding gclo.slnx.
    /// </summary>
    private static string ResolveExePath()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("GCLO_UITEST_EXE");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            if (!File.Exists(fromEnv))
            {
                throw new FileNotFoundException(
                    $"GCLO_UITEST_EXE points at '{fromEnv}', which does not exist.", fromEnv);
            }
            return fromEnv;
        }

        for (DirectoryInfo? current = new(AppContext.BaseDirectory);
            current is not null;
            current = current.Parent)
        {
            if (!File.Exists(Path.Combine(current.FullName, "gclo.slnx")))
            {
                continue;
            }

            string exePath = Path.Combine(
                current.FullName, "gclo", "bin", "x64", "Debug",
                "net10.0-windows10.0.19041.0", "win-x64", "gclo.exe");
            if (!File.Exists(exePath))
            {
                throw new FileNotFoundException(
                    $"gclo.exe not found at '{exePath}'. Build the app first: "
                    + "dotnet build gclo/gclo.csproj -p:Platform=x64 -p:WindowsPackageType=None "
                    + "— or set GCLO_UITEST_EXE to the exe to test.",
                    exePath);
            }
            return exePath;
        }

        throw new FileNotFoundException(
            $"Could not find the repo root (no gclo.slnx above '{AppContext.BaseDirectory}'). "
            + "Set GCLO_UITEST_EXE to the gclo.exe to test.");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Order matters: take the app down first, dispose the automation only after
        // the process is gone, then sweep the temp data directory. Every step is
        // best-effort so one failure cannot mask the test outcome.
        try
        {
            App?.Close(); // FlaUI kills the process itself if closing stalls
        }
        catch
        {
            // The process may already be gone; Kill below double-checks.
        }
        try
        {
            if (App is { HasExited: false })
            {
                App.Kill();
            }
        }
        catch
        {
            // Best effort only.
        }
        App?.Dispose();
        Automation?.Dispose();

        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch
        {
            // A straggling handle keeps the directory; it lives under %TEMP% anyway.
        }
        GC.SuppressFinalize(this);
    }
}
