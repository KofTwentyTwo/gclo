/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;


namespace gclo.Engine;


/// <summary>
/// Turns what a user typed as the address of a Gitea server into the server's base
/// address, or says why it cannot be one. The base address is where the token of an
/// account is sent and where its repositories are cloned from, so it is held to a
/// few rules: HTTPS only, no user name or password in it, nothing after the path.
/// </summary>
public static class GiteaServerUrl
{
   /// <summary>
   /// The base address for <paramref name="text"/>: <c>https</c>, the host (and a
   /// port that is not 443), and the path with one slash at its end, so that paths
   /// are appended to it. A host typed without a scheme is taken as <c>https</c>.
   /// </summary>
   /// <param name="text">What was typed, for example <c>git.example.com</c> or <c>https://example.com/gitea</c>.</param>
   /// <param name="server">The base address; null when there is none.</param>
   /// <param name="reason">Why <paramref name="text"/> is not a server address; null when it is one.</param>
   public static bool TryNormalize(string? text, [NotNullWhen(true)] out Uri? server, [NotNullWhen(false)] out string? reason)
   {
      server = null;
      string typed = (text ?? "").Trim();
      if(typed.Length == 0)
      {
         reason = "it is empty";
         return false;
      }

      if(typed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
      {
         reason = "it contains a space or a control character";
         return false;
      }

      if(!typed.Contains("://", StringComparison.Ordinal))
      {
         typed = "https://" + typed;
      }

      if(!Uri.TryCreate(typed, UriKind.Absolute, out Uri? parsed) || parsed.Host.Length == 0)
      {
         reason = "it is not an address";
         return false;
      }

      if(!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
      {
         ///////////////////////////////////////////////////////////////////////////
         // The token travels in every request: never over a connection that is   //
         // not encrypted, and there is no setting that allows it.                //
         ///////////////////////////////////////////////////////////////////////////
         reason = "it has to begin with https://";
         return false;
      }

      if(parsed.UserInfo.Length > 0)
      {
         reason = "it contains a user name or a password; the token is entered separately";
         return false;
      }

      if(parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
      {
         reason = "it has a '?' or a '#' part; only the server's own address is needed";
         return false;
      }

      string path = parsed.AbsolutePath.EndsWith('/') ? parsed.AbsolutePath : parsed.AbsolutePath + "/";
      server = new UriBuilder(Uri.UriSchemeHttps, parsed.IdnHost, parsed.IsDefaultPort ? -1 : parsed.Port, path).Uri;
      reason = null;
      return true;
   }
}
