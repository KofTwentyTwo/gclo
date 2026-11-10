/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Text;
using System.Text.RegularExpressions;
using gclo.Engine;


namespace gclo.Cli.Tests;


/// <summary>
/// <see cref="Terminal"/> is the one place the CLI writes to the console, and what it
/// writes is safe for the terminal in the console's own encoding (#143).
/// </summary>
public sealed partial class TerminalTests
{
   private const char Escape = (char)0x1B;

   static TerminalTests()
   {
      // Legacy code pages are in the framework but not registered by default.
      Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
   }



   // ---------------------------------------------------------------- Unicode consoles

   [Theory]
   [InlineData(65001)] // UTF-8
   [InlineData(1200)] // UTF-16
   [InlineData(12000)] // UTF-32
   public void Safe_OnAUnicodeConsole_IsTheVisibleFormAndNothingMore(int codePage)
   {
      var encoding = Encoding.GetEncoding(codePage);
      string text = "caf" + (char)0xE9 + " " + (char)0x65E5 + (char)0x672C + " " + (char)0x2190 + " plain";

      Assert.Same(text, Terminal.Safe(text, encoding));
      Assert.Equal(DisplayText.Escape("a\nb" + Escape + "[2J"), Terminal.Safe("a\nb" + Escape + "[2J", encoding));
      Assert.Equal("", Terminal.Safe(null, encoding));
   }



   [Fact]
   public void Safe_NullEncoding_IsACallerError()
   {
      Assert.Throws<ArgumentNullException>(() => Terminal.Safe("text", null!));
   }



   // ---------------------------------------------------------------- legacy code pages

   [Theory]
   [InlineData(437)]
   [InlineData(850)]
   public void Safe_OnALegacyCodePage_ACharacterItWouldTurnIntoAControlByte_IsShownVisibly(int codePage)
   {
      var encoding = Encoding.GetEncoding(codePage);
      // Printable characters that are not control characters, so the first pass leaves
      // them alone; code page 437 "best-fits" the arrow to ESC and the inverse circle
      // to a line feed.
      string hostile = "x:" + (char)0x2190 + "[2J" + (char)0x25D9 + "other  Done";
      Assert.Same(hostile, DisplayText.Escape(hostile));

      string safe = Terminal.Safe(hostile, encoding);

      Assert.Equal("x:" + Visible(0x2190) + "[2J" + Visible(0x25D9) + "other  Done", safe);
      AssertNoControlBytes(encoding.GetBytes(safe));
   }



   [Theory]
   [InlineData(437)]
   [InlineData(850)]
   [InlineData(1252)]
   [InlineData(932)]
   public void Safe_OnALegacyCodePage_NoCharacterOfTheBasicPlane_ReachesTheTerminalAsAControlByte(int codePage)
   {
      var encoding = Encoding.GetEncoding(codePage);
      var failures = new List<string>();
      for(int code = 0; code <= 0xFFFF; code++)
      {
         if(code is >= 0xD800 and <= 0xDFFF)
         {
            continue; // a lone surrogate is covered below
         }

         byte[] bytes = encoding.GetBytes(Terminal.Safe("a" + (char)code + "b", encoding));
         if(bytes.Any(b => b < 0x20 || b == 0x7F))
         {
            failures.Add(code.ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
         }
      }

      Assert.True(failures.Count == 0, "control bytes for: " + string.Join(' ', failures.Take(40)));
   }



   [Theory]
   [InlineData(437)]
   [InlineData(1252)]
   public void Safe_OnALegacyCodePage_KeepsWhatTheCodePageCanShow_AndEscapesWhatItCannot(int codePage)
   {
      var encoding = Encoding.GetEncoding(codePage);
      string emoji = char.ConvertFromUtf32(0x1F600);

      // e-acute is in both code pages; the kanji and the emoji are in neither.
      string safe = Terminal.Safe("caf" + (char)0xE9 + " " + (char)0x65E5 + " " + emoji + " end", encoding);

      Assert.Equal("caf" + (char)0xE9 + " " + Visible(0x65E5) + " " + Visible(emoji[0]) + Visible(emoji[1]) + " end", safe);
   }



   [Fact]
   public void Safe_IsStable_WhenAppliedTwice()
   {
      var encoding = Encoding.GetEncoding(437);
      string once = Terminal.Safe("a\nb" + (char)0x2190 + (char)0x65E5, encoding);

      Assert.Equal(once, Terminal.Safe(once, encoding));
   }



   // ---------------------------------------------------------------- the writers

   [Fact]
   public void OutAndError_WriteOneSafeLine_ToTheirOwnStream()
   {
      using var console = new ConsoleCapture();

      Terminal.Out("repo" + Escape + "[31m  Done\nforged  Done");
      Terminal.Error("repo  Failed  bad" + Escape + "[2J\nforged");

      Assert.Equal("repo" + Visible(0x1B) + "[31m  Done" + Visible(0x0A) + "forged  Done" + Environment.NewLine, console.Out);
      Assert.Equal("repo  Failed  bad" + Visible(0x1B) + "[2J" + Visible(0x0A) + "forged" + Environment.NewLine, console.Error);
   }



   [Fact]
   public void OutVerbatim_IsForFixedTextAndJson_AndChangesNothing()
   {
      using var console = new ConsoleCapture();

      Terminal.OutVerbatim("Usage:\n  gclo sync");

      Assert.Equal("Usage:\n  gclo sync" + Environment.NewLine, console.Out);
      Assert.Equal("", console.Error);
   }



   [Fact]
   public void ErrorPrompt_StaysOnItsLine_UntilTheLineBreak()
   {
      using var console = new ConsoleCapture();

      Terminal.ErrorPrompt("Token: ");
      Assert.Equal("Token: ", console.Error);

      Terminal.ErrorLineBreak();
      Assert.Equal("Token: " + Environment.NewLine, console.Error);
      Assert.Equal("", console.Out);
   }



   // ---------------------------------------------------------------- nothing bypasses it

   [Fact]
   public void NoOtherFileOfTheCli_WritesToTheConsoleDirectly()
   {
      // Every line the CLI prints goes through Terminal, so a new message cannot
      // forget to be made safe. The check is on the source: an output that bypasses
      // Terminal would otherwise pass every behavioural test that does not happen
      // to feed it hostile text.
      string cli = Path.Combine(RepoRoot, "gclo.Cli");
      var offenders = new List<string>();
      foreach(string file in Directory.EnumerateFiles(cli, "*.cs", SearchOption.TopDirectoryOnly))
      {
         if(string.Equals(Path.GetFileName(file), "Terminal.cs", StringComparison.Ordinal))
         {
            continue;
         }

         string[] lines = File.ReadAllLines(file);
         for(int i = 0; i < lines.Length; i++)
         {
            if(DirectConsoleWrite().IsMatch(lines[i]))
            {
               offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
         }
      }

      Assert.True(offenders.Count == 0, "write through Terminal instead:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
   }



   // ---------------------------------------------------------------- helpers

   private static string RepoRoot
   {
      get
      {
         DirectoryInfo? current = new(AppContext.BaseDirectory);
         while(current is not null && !File.Exists(Path.Combine(current.FullName, "gclo.slnx")))
         {
            current = current.Parent;
         }
         return current?.FullName ?? throw new FileNotFoundException("gclo.slnx not found above the test directory");
      }
   }



   private static void AssertNoControlBytes(byte[] bytes)
       => Assert.DoesNotContain(bytes, b => b < 0x20 || b == 0x7F);



   /// <summary>The visible form of one UTF-16 code unit: a backslash, "u", four upper-case hex digits.</summary>
   private static string Visible(int unit)
       => "\\" + "u" + unit.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);



   [GeneratedRegex(@"\bConsole\s*\.\s*(Out|Error)\b|\bConsole\s*\.\s*Write(Line)?\s*\(", RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex DirectConsoleWrite();
}
