/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Text;


namespace gclo.Engine;


/// <summary>
/// Makes text that came from somewhere else safe to put in a log line. Repository
/// content reaches the log: a file path in a git tree may contain a newline, an escape
/// character, or a character that reorders or hides the text around it, and such a
/// path is exactly what gclo reports when it cannot be created on Windows. Left alone,
/// a newline starts what reads as a new log entry (#143).
/// </summary>
/// <remarks>
/// <para>
/// What becomes visible is decided per code point, by Unicode category: control
/// characters (C0, DEL, C1), line and paragraph separators, format characters (the
/// text-direction marks, embeddings, overrides and isolates; zero-width and other
/// invisible characters; tag characters), and a surrogate that has no partner. The
/// zero-width joiner and non-joiner stay: emoji sequences and several scripts are
/// written with them. Everything else, in any script, is left as it is.
/// </para>
/// <para>
/// The visible form is a backslash, the letter u and four upper-case hexadecimal
/// digits, once per UTF-16 code unit. A path that already contains those six
/// characters reads the same way; the form is for people, and nothing reads it back.
/// </para>
/// <para>
/// A terminal needs more than this: what reaches it is bytes in the console's code
/// page, and a legacy code page turns some ordinary characters into control bytes.
/// The command line writes through its own layer for that.
/// </para>
/// </remarks>
public static class DisplayText
{
   private const string HexDigits = "0123456789ABCDEF";

   private static readonly string[] s_lineBreaks = ["\r\n", "\n", "\r"];



   /// <summary>
   /// <paramref name="text"/> with every character described in the type remarks
   /// replaced by its visible form; the result is always one line. Text that needs no
   /// change comes back as the same string. Null gives an empty string.
   /// </summary>
   public static string Escape(string? text)
   {
      if(string.IsNullOrEmpty(text))
      {
         return "";
      }

      int first = 0;
      while(first < text.Length)
      {
         if(IsUnsafeAt(text, first, out int length))
         {
            break;
         }
         first += length;
      }

      if(first == text.Length)
      {
         return text;
      }

      var builder = new StringBuilder(text.Length + 16);
      builder.Append(text, 0, first);
      for(int i = first; i < text.Length;)
      {
         bool unsafeHere = IsUnsafeAt(text, i, out int length);
         for(int unit = 0; unit < length; unit++)
         {
            if(unsafeHere)
            {
               AppendVisible(builder, text[i + unit]);
            }
            else
            {
               builder.Append(text[i + unit]);
            }
         }
         i += length;
      }

      return builder.ToString();
   }



   /// <summary>
   /// <paramref name="text"/> as a block of lines: its own line breaks are kept, every
   /// line is escaped like <see cref="Escape"/> and starts with
   /// <paramref name="indent"/>. With a non-empty indent no line of the block can be
   /// taken for something that starts in the first column, such as a log entry's
   /// timestamp. Null or empty text gives an empty string.
   /// </summary>
   public static string EscapeLines(string? text, string indent)
   {
      ArgumentNullException.ThrowIfNull(indent);
      if(string.IsNullOrEmpty(text))
      {
         return "";
      }

      return string.Join(
          Environment.NewLine,
          text.Split(s_lineBreaks, StringSplitOptions.None).Select(line => indent + Escape(line)));
   }



   /// <summary>
   /// Appends the visible form of one UTF-16 code unit to <paramref name="builder"/>:
   /// a backslash, the letter u, and four upper-case hexadecimal digits.
   /// </summary>
   public static void AppendVisible(StringBuilder builder, char unit)
   {
      ArgumentNullException.ThrowIfNull(builder);
      builder.Append('\\').Append('u');
      for(int shift = 12; shift >= 0; shift -= 4)
      {
         builder.Append(HexDigits[(unit >> shift) & 0xF]);
      }
   }



   /// <summary>
   /// Whether the code point that starts at <paramref name="index"/> must be made
   /// visible, and how many UTF-16 code units it takes (two for a surrogate pair).
   /// </summary>
   private static bool IsUnsafeAt(string text, int index, out int length)
   {
      char c = text[index];
      if(char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
      {
         length = 2;
         return IsUnsafeCategory(CharUnicodeInfo.GetUnicodeCategory(char.ConvertToUtf32(c, text[index + 1])));
      }

      length = 1;
      if(char.IsSurrogate(c))
      {
         return true; // half of a pair: not a character, and writers replace it with something else
      }

      // The zero-width non-joiner and joiner (0x200C, 0x200D) are format characters
      // that ordinary text needs.
      return c is not ((char)0x200C or (char)0x200D) && IsUnsafeCategory(CharUnicodeInfo.GetUnicodeCategory(c));
   }



   private static bool IsUnsafeCategory(UnicodeCategory category)
       => category is UnicodeCategory.Control
           or UnicodeCategory.Format
           or UnicodeCategory.LineSeparator
           or UnicodeCategory.ParagraphSeparator;
}
