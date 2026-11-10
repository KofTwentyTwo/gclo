/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Text;


namespace gclo.Engine.Tests;


/// <summary>
/// <see cref="DisplayText"/> turns characters that would break a log line, or reorder
/// or hide the text around them, into visible text, and leaves everything else alone
/// (#143).
/// </summary>
public sealed class DisplayTextTests
{
   private const char Escape = (char)0x1B;



   [Theory]
   [InlineData("plain text")]
   [InlineData("src/app/Main.cs")]
   [InlineData("café/日本語.txt")]
   [InlineData("emoji 😀 stays")]
   [InlineData(@"C:\Users\someone\src\repo")]
   [InlineData(" ")]
   public void Escape_OrdinaryText_ComesBackUnchanged_AsTheSameString(string text)
   {
      Assert.Same(text, DisplayText.Escape(text));
   }



   [Fact]
   public void Escape_TextThatNeedsJoiners_ComesBackUnchanged()
   {
      // A family emoji is three people held together by zero-width joiners, and
      // Persian uses the zero-width non-joiner inside ordinary words.
      string family = char.ConvertFromUtf32(0x1F468) + (char)0x200D + char.ConvertFromUtf32(0x1F469) + (char)0x200D + char.ConvertFromUtf32(0x1F467);
      string persian = "" + (char)0x0645 + (char)0x06CC + (char)0x200C + (char)0x062E;

      Assert.Same(family, DisplayText.Escape(family));
      Assert.Same(persian, DisplayText.Escape(persian));
   }



   [Theory]
   [InlineData(null)]
   [InlineData("")]
   public void Escape_NothingToShow_IsEmpty(string? text)
   {
      Assert.Equal("", DisplayText.Escape(text));
   }



   [Fact]
   public void Escape_ControlCharacters_BecomeVisible_OnOneLine()
   {
      Assert.Equal("a" + Visible(0x0A) + "b", DisplayText.Escape("a\nb"));
      Assert.Equal("a" + Visible(0x0D) + Visible(0x0A) + "b", DisplayText.Escape("a\r\nb"));
      Assert.Equal("col1" + Visible(0x09) + "col2", DisplayText.Escape("col1\tcol2"));
      Assert.Equal(Visible(0x1B) + "[2Jwiped", DisplayText.Escape(Escape + "[2Jwiped"));
      Assert.Equal("bell" + Visible(0x07), DisplayText.Escape("bell" + (char)0x07));
      Assert.Equal("nul" + Visible(0x00), DisplayText.Escape("nul" + (char)0x00));
      Assert.Equal("del" + Visible(0x7F), DisplayText.Escape("del" + (char)0x7F));
      Assert.Equal("nel" + Visible(0x85), DisplayText.Escape("nel" + (char)0x85));
      Assert.Equal("csi" + Visible(0x9B) + "31m", DisplayText.Escape("csi" + (char)0x9B + "31m"));
   }



   [Theory]
   [InlineData(0x2028)] // line separator
   [InlineData(0x2029)] // paragraph separator
   [InlineData(0x200E)] // left-to-right mark
   [InlineData(0x200F)] // right-to-left mark
   [InlineData(0x061C)] // Arabic letter mark
   [InlineData(0x202A)] // left-to-right embedding
   [InlineData(0x202E)] // right-to-left override
   [InlineData(0x2066)] // left-to-right isolate
   [InlineData(0x2069)] // pop directional isolate
   [InlineData(0x206A)] // deprecated format characters
   [InlineData(0x200B)] // zero-width space
   [InlineData(0x2060)] // word joiner
   [InlineData(0xFEFF)] // zero-width no-break space (byte order mark)
   [InlineData(0x00AD)] // soft hyphen
   public void Escape_SeparatorsDirectionAndInvisibleCharacters_BecomeVisible(int code)
   {
      string text = "invoice" + (char)code + "fdp.exe";

      string escaped = DisplayText.Escape(text);

      Assert.Equal("invoice" + Visible(code) + "fdp.exe", escaped);
      Assert.DoesNotContain((char)code, escaped);
   }



   [Theory]
   [InlineData(0x2027)] // hyphenation point: punctuation
   [InlineData(0x202F)] // narrow no-break space: a space
   [InlineData(0x2065)] // unassigned
   [InlineData(0x200C)] // zero-width non-joiner
   [InlineData(0x200D)] // zero-width joiner
   [InlineData(0xFE0F)] // variation selector: a mark, used by emoji
   [InlineData(0xE000)] // private use
   public void Escape_CharactersThatOrdinaryTextUses_AreLeftAlone(int code)
   {
      string text = "a" + (char)code + "b";

      Assert.Same(text, DisplayText.Escape(text));
   }



   [Fact]
   public void Escape_TagCharacters_WhichAreInvisibleAndCanCarryHiddenText_BecomeVisible_AsTheirTwoCodeUnits()
   {
      string tag = char.ConvertFromUtf32(0xE0041); // "tag latin capital letter a"

      string escaped = DisplayText.Escape("a" + tag + "b");

      Assert.Equal("a" + Visible(tag[0]) + Visible(tag[1]) + "b", escaped);
   }



   [Fact]
   public void Escape_HalfOfASurrogatePair_BecomesVisible_WhereverItStands()
   {
      char high = char.ConvertFromUtf32(0x1F600)[0];
      char low = char.ConvertFromUtf32(0x1F600)[1];

      Assert.Equal("a" + Visible(high) + "b", DisplayText.Escape("a" + high + "b"));
      Assert.Equal("a" + Visible(low) + "b", DisplayText.Escape("a" + low + "b"));
      Assert.Equal("a" + Visible(high), DisplayText.Escape("a" + high));
      Assert.Equal(Visible(low) + Visible(high), DisplayText.Escape("" + low + high));
   }



   [Fact]
   public void Escape_KeepsEverythingBeforeAndAfterTheFirstCharacterItChanges()
   {
      string emoji = char.ConvertFromUtf32(0x1F600);
      string text = "start " + emoji + " " + Escape + " middle \n " + emoji + " end";

      Assert.Equal(
          "start " + emoji + " " + Visible(0x1B) + " middle " + Visible(0x0A) + " " + emoji + " end",
          DisplayText.Escape(text));
   }



   [Fact]
   public void EscapeLines_KeepsTheLines_IndentsEachOne_AndEscapesWithinThem()
   {
      string text = "first\nsecond " + Escape + "[0m\r\nthird\rfourth\n";

      string block = DisplayText.EscapeLines(text, "   ");

      Assert.Equal(
          new[] { "   first", "   second " + Visible(0x1B) + "[0m", "   third", "   fourth", "   " },
          block.Split(Environment.NewLine));
   }



   [Fact]
   public void EscapeLines_NoIndent_StillOneEscapedLinePerLine()
   {
      Assert.Equal("a" + Environment.NewLine + "b", DisplayText.EscapeLines("a\nb", ""));
   }



   [Theory]
   [InlineData(null)]
   [InlineData("")]
   public void EscapeLines_NothingToShow_IsEmpty(string? text)
   {
      Assert.Equal("", DisplayText.EscapeLines(text, "   "));
   }



   [Fact]
   public void EscapeLines_NullIndent_IsACallerError()
   {
      Assert.Throws<ArgumentNullException>(() => DisplayText.EscapeLines("text", null!));
   }



   [Fact]
   public void AppendVisible_WritesBackslashUAndFourUpperCaseHexDigits()
   {
      var builder = new StringBuilder("x");

      DisplayText.AppendVisible(builder, (char)0x1B);
      DisplayText.AppendVisible(builder, (char)0xFEFF);
      DisplayText.AppendVisible(builder, (char)0x000A);

      Assert.Equal("x" + Visible(0x1B) + Visible(0xFEFF) + Visible(0x0A), builder.ToString());
      Assert.Equal(6, Visible(0x1B).Length);
      Assert.Throws<ArgumentNullException>(() => DisplayText.AppendVisible(null!, 'a'));
   }



   /// <summary>The expected visible form: a backslash, "u", and four upper-case hex digits.</summary>
   private static string Visible(int code)
       => "\\" + "u" + code.ToString("X4", CultureInfo.InvariantCulture);
}
