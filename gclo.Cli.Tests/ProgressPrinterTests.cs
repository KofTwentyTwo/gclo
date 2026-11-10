/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.Engine;


namespace gclo.Cli.Tests;


/// <summary>
/// Covers <see cref="ProgressPrinter"/>: transition deduplication, which stream each
/// output mode writes to, and the failure/sanitized records the summary is built from.
/// </summary>
public sealed class ProgressPrinterTests
{
   private static readonly InvalidPathInfo s_badPath = new("bad:name.txt", "invalid on Windows", "bad_name.txt");



   [Fact]
   public void Text_PrintsTransitionsOnStdout_FailuresOnStderr_AndDedupesRepeats()
   {
      using var console = new ConsoleCapture();
      var printer = new ProgressPrinter(OutputMode.Text);

      printer.Report(new RepoProgress("a", SyncStatus.Queued));
      printer.Report(new RepoProgress("a", SyncStatus.Cloning, Percent: 0.1));
      printer.Report(new RepoProgress("a", SyncStatus.Cloning, Percent: 0.5)); // same status: suppressed
      printer.Report(new RepoProgress("a", SyncStatus.Done));
      printer.Report(new RepoProgress("b", SyncStatus.Failed, "boom"));

      Assert.Equal(["a  Queued", "a  Cloning", "a  Done"], Lines(console.Out));
      Assert.Equal(["b  Failed  boom"], Lines(console.Error));
      Assert.Equal("boom", Assert.Single(printer.Failures).Error);
      Assert.Null(printer.Failures[0].InvalidPaths);
   }



   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public void TextModes_ShowControlCharactersFromRepositoryContentAsVisibleText(bool quiet)
   {
      using var console = new ConsoleCapture();
      var printer = new ProgressPrinter(quiet ? OutputMode.Quiet : OutputMode.Text);
      string escape = ((char)0x1B).ToString();
      // An error that quotes a path from a repository's tree: a newline that would
      // start a fake line, and an escape sequence the terminal would obey.
      string error = "invalid path 'a\nother  Done" + escape + "[2J'";

      printer.Report(new RepoProgress("we" + escape + "[31mird", SyncStatus.Cloning));
      printer.Report(new RepoProgress("repo", SyncStatus.Failed, error));

      string line = Assert.Single(Lines(console.Error));
      Assert.StartsWith("repo  Failed  invalid path 'a", line, StringComparison.Ordinal);
      Assert.Contains(Visible(0x0A) + "other  Done", line);
      Assert.Contains(Visible(0x1B) + "[2J'", line);
      // Compared as characters: a culture-aware text search treats the escape character as ignorable.
      Assert.DoesNotContain((char)0x1B, console.Error);
      Assert.DoesNotContain((char)0x1B, console.Out);
      if(!quiet)
      {
         Assert.Equal("we" + Visible(0x1B) + "[31mird  Cloning", Assert.Single(Lines(console.Out)));
      }
      // The record for the summary document keeps the text as it was: JSON has its own escaping.
      Assert.Equal(error, Assert.Single(printer.Failures).Error);
   }



   [Fact]
   public void Quiet_PrintsOnlyFailures()
   {
      using var console = new ConsoleCapture();
      var printer = new ProgressPrinter(OutputMode.Quiet);

      printer.Report(new RepoProgress("a", SyncStatus.Cloning));
      printer.Report(new RepoProgress("a", SyncStatus.Done));
      printer.Report(new RepoProgress("b", SyncStatus.Failed, null));

      Assert.Equal("", console.Out);
      Assert.Equal(["b  Failed  unknown error"], Lines(console.Error));
   }



   [Fact]
   public void Json_PrintsNothing_ButRecordsFailuresWithTheirInvalidPaths()
   {
      using var console = new ConsoleCapture();
      var printer = new ProgressPrinter(OutputMode.Json);

      printer.Report(new RepoProgress("a", SyncStatus.Cloning));
      printer.Report(new RepoProgress("b", SyncStatus.Failed, "paths", InvalidPaths: [s_badPath]));

      Assert.Equal("", console.Out);
      Assert.Equal("", console.Error);
      SyncFailure failure = Assert.Single(printer.Failures);
      SyncInvalidPath path = Assert.Single(failure.InvalidPaths!);
      Assert.Equal(("bad:name.txt", "invalid on Windows", "bad_name.txt"), (path.Path, path.Reason, path.SuggestedName));
   }



   [Fact]
   public void JsonLines_PrintsOneObjectPerTransition_IncludingFailures_OnStdout()
   {
      using var console = new ConsoleCapture();
      var printer = new ProgressPrinter(OutputMode.JsonLines);

      printer.Report(new RepoProgress("a", SyncStatus.Pulling));
      printer.Report(new RepoProgress("a", SyncStatus.Pulling)); // suppressed
      printer.Report(new RepoProgress("b", SyncStatus.Failed, "boom"));

      Assert.Equal(
          [
              "{\"type\":\"progress\",\"repo\":\"a\",\"status\":\"Pulling\",\"error\":null}",
                "{\"type\":\"progress\",\"repo\":\"b\",\"status\":\"Failed\",\"error\":\"boom\"}",
            ],
          Lines(console.Out));
      Assert.Equal("", console.Error);
   }



   [Fact]
   public void RecordSanitized_IsExposedForTheSummary()
   {
      var printer = new ProgressPrinter(OutputMode.Json);

      printer.RecordSanitized(new SyncSanitized("a", 2, 1, ["dup"]));

      SyncSanitized record = Assert.Single(printer.Sanitized);
      Assert.Equal((2, 1), (record.Renamed, record.Skipped));
   }



   [Fact]
   public void MapInvalidPaths_EmptyOrNull_IsNull()
   {
      Assert.Null(ProgressPrinter.MapInvalidPaths(null));
      Assert.Null(ProgressPrinter.MapInvalidPaths([]));
   }



   /// <summary>The visible form of one UTF-16 code unit: a backslash, "u", four upper-case hex digits.</summary>
   private static string Visible(int unit)
       => "\\" + "u" + unit.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);



   private static string[] Lines(string text)
       => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
}
