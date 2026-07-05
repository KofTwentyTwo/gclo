using gclo.Engine;

namespace gclo.Cli.Tests;

/// <summary>Covers the console progress printer: dedup, stdout/stderr routing, and gating.</summary>
public sealed class ProgressPrinterTests
{
    [Fact]
    public void Report_PrintsTransitionsToStdout_AndSuppressesRepeats()
    {
        using var console = new ConsoleCapture();
        var printer = new ProgressPrinter(printProgress: true, printFailures: true);

        printer.Report(new RepoProgress("alpha", SyncStatus.Cloning));
        printer.Report(new RepoProgress("alpha", SyncStatus.Cloning, Percent: 0.5)); // repeat status: suppressed
        printer.Report(new RepoProgress("alpha", SyncStatus.Done));

        string[] lines = console.Out.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["alpha  Cloning", "alpha  Done"], lines);
    }

    [Fact]
    public void Report_Failure_GoesToStderr_AndIsCollected()
    {
        using var console = new ConsoleCapture();
        var printer = new ProgressPrinter(printProgress: true, printFailures: true);

        printer.Report(new RepoProgress("bravo", SyncStatus.Failed, "boom"));

        Assert.Contains("bravo  Failed  boom", console.Error);
        var failure = Assert.Single(printer.Failures);
        Assert.Equal("bravo", failure.Repo);
        Assert.Equal("boom", failure.Error);
    }

    [Fact]
    public void Report_Failure_NullError_RecordsUnknown()
    {
        using var console = new ConsoleCapture();
        var printer = new ProgressPrinter(printProgress: false, printFailures: false);

        printer.Report(new RepoProgress("bravo", SyncStatus.Failed));

        Assert.Equal("unknown error", Assert.Single(printer.Failures).Error);
        Assert.Equal("", console.Error); // printFailures off: nothing on stderr
    }

    [Fact]
    public void Report_ProgressOff_PrintsNoTransitions()
    {
        using var console = new ConsoleCapture();
        var printer = new ProgressPrinter(printProgress: false, printFailures: true);

        printer.Report(new RepoProgress("alpha", SyncStatus.Cloning));

        Assert.Equal("", console.Out);
    }
}
