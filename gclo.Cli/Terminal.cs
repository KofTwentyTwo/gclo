/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Text;
using gclo.Engine;


namespace gclo.Cli;


/// <summary>
/// The one place the command line writes to standard output and standard error. Text
/// that is assembled at run time can quote a repository, a server, or a file on disk,
/// so it goes out through <see cref="Out"/> and <see cref="Error"/>, which make it
/// safe for a terminal. Only text that is fixed in the program (help, the version) or
/// produced by the JSON encoder (which emits ASCII and escapes everything else) is
/// written as it is, through <see cref="OutVerbatim"/> (#143).
/// </summary>
/// <remarks>
/// Safe for a terminal means two things. Characters that would break a line, steer
/// the terminal, or reorder or hide the text around them are shown in visible form
/// (see <see cref="DisplayText"/>). And because what reaches the terminal is bytes in
/// the console's code page, a character that code page cannot represent is shown the
/// same way: a legacy code page otherwise "best-fits" it to a different byte, and for
/// some ordinary printable characters that byte is a control character (on code page
/// 437, a left arrow becomes ESC).
/// </remarks>
internal static class Terminal
{
   private static bool s_isReadingInput;



   /// <summary>Writes one line of run-time text to standard output, made safe for the terminal.</summary>
   public static void Out(string text) => Console.Out.WriteLine(Safe(text, Console.Out.Encoding));



   /// <summary>Writes one line of run-time text to standard error, made safe for the terminal.</summary>
   public static void Error(string text) => Console.Error.WriteLine(Safe(text, Console.Error.Encoding));



   /// <summary>
   /// Writes text to standard output exactly as given. For text fixed in the program
   /// and for JSON documents only; never for anything that quotes run-time data.
   /// </summary>
   public static void OutVerbatim(string text) => Console.Out.WriteLine(text);



   /// <summary>Writes a fixed prompt to standard error, without a line break after it.</summary>
   public static void ErrorPrompt(string fixedText) => Console.Error.Write(fixedText);



   /// <summary>
   /// True while the process waits for a line on standard input (the token prompt).
   /// A Ctrl+C then ends the process: there is nothing to finish gracefully, and
   /// the read would otherwise go on waiting.
   /// </summary>
   public static bool IsReadingInput
   {
      get => Volatile.Read(ref s_isReadingInput);
      set => Volatile.Write(ref s_isReadingInput, value);
   }



   /// <summary>Ends the current line on standard error.</summary>
   public static void ErrorLineBreak() => Console.Error.WriteLine();



   /// <summary>
   /// <paramref name="text"/> as one line that is safe to send to a terminal whose
   /// output encoding is <paramref name="encoding"/>.
   /// </summary>
   internal static string Safe(string? text, Encoding encoding)
   {
      ArgumentNullException.ThrowIfNull(encoding);

      string visible = DisplayText.Escape(text);
      if(IsUnicode(encoding))
      {
         return visible;
      }

      StringBuilder? builder = null;
      int index = 0;
      foreach(Rune rune in visible.EnumerateRunes())
      {
         int length = rune.Utf16SequenceLength;
         if(rune.IsAscii || RoundTrips(encoding, visible.AsSpan(index, length)))
         {
            builder?.Append(visible, index, length);
         }
         else
         {
            builder ??= new StringBuilder(visible.Length + 16).Append(visible, 0, index);
            for(int unit = 0; unit < length; unit++)
            {
               DisplayText.AppendVisible(builder, visible[index + unit]);
            }
         }
         index += length;
      }

      return builder?.ToString() ?? visible;
   }



   /// <summary>UTF-8, UTF-16 and UTF-32 represent every character; nothing is best-fitted.</summary>
   private static bool IsUnicode(Encoding encoding)
       => encoding.CodePage is 65001 or 1200 or 1201 or 12000 or 12001;



   /// <summary>True when <paramref name="character"/> survives encoding and decoding unchanged.</summary>
   private static bool RoundTrips(Encoding encoding, ReadOnlySpan<char> character)
   {
      Span<byte> bytes = stackalloc byte[16];
      Span<char> back = stackalloc char[8];
      int byteCount = encoding.GetBytes(character, bytes);
      int charCount = encoding.GetChars(bytes[..byteCount], back);
      return back[..charCount].SequenceEqual(character);
   }
}
