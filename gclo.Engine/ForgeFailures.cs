/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine;


/// <summary>
/// Words the failure translations of every forge share: a server that could not be
/// reached, and a server's own reason made safe to put into a message.
/// </summary>
internal static class ForgeFailures
{
   /// <summary>The longest reason from a server that is put into a message.</summary>
   private const int MaxReasonLength = 300;



   /// <summary>
   /// The words for a request that never got an answer. Chosen by the kind of failure,
   /// never copied from the failure's own text: that text can name a proxy together
   /// with the user name and password used for it.
   /// </summary>
   /// <param name="server">What could not be reached, as the message names it ("GitHub", a host).</param>
   /// <param name="error">The kind of failure.</param>
   public static string Unreachable(string server, HttpRequestError error)
   {
      string what = error switch
      {
         HttpRequestError.NameResolutionError => "its name could not be resolved (DNS)",
         HttpRequestError.ConnectionError => "the connection could not be made",
         HttpRequestError.SecureConnectionError
             => "a secure (TLS) connection could not be established; a proxy or a security product that inspects traffic is a common cause",
         HttpRequestError.ProxyTunnelError => "the proxy on the way refused the connection",
         HttpRequestError.Unknown => "the request failed on the way",
         _ => $"the request failed on the way ({error})",
      };
      return $"{server} could not be reached: {what}. Check the network connection and retry.";
   }



   /// <summary>
   /// A reason from a server as one line of bounded length that ends a sentence; empty
   /// when there is none.
   /// </summary>
   public static string OneLine(string? text)
   {
      string line = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
      if(line.Length == 0)
      {
         return "";
      }
      if(line.Length > MaxReasonLength)
      {
         // Cut between characters, never through a pair of code units.
         int keep = char.IsHighSurrogate(line[MaxReasonLength - 1]) ? MaxReasonLength - 1 : MaxReasonLength;
         return line[..keep] + "...";
      }
      return line[^1] is '.' or '!' or '?' ? line : line + ".";
   }
}
