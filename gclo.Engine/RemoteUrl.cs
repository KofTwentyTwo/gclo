/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using System.Globalization;


namespace gclo.Engine;


/// <summary>
/// Decides two things about git remote URLs, both from the clone URL a listing returned
/// (the "expected" URL): whether an existing folder's <c>origin</c> names that same
/// repository, and whether a remote that asks for credentials is one the token is
/// meant for. A sync must never report a different repository as updated, and must
/// never hand the token to a remote on another host (#139).
/// </summary>
internal static class RemoteUrl
{
   private static readonly char[] s_separators = ['/', '\\'];



   /// <summary>
   /// True when <paramref name="actual"/> (a folder's configured origin) names the same
   /// repository as <paramref name="expected"/> (the listed clone URL). Network URLs
   /// are compared by host and path, ignoring scheme, credentials, letter case, a
   /// trailing slash and a trailing <c>.git</c>, so an origin switched to SSH still
   /// matches its HTTPS listing; the port counts only between two HTTP(S) URLs, where
   /// it tells two servers on one host apart. Local paths (the offline test fixtures,
   /// or a caller's own mirror) are compared as full paths. A network URL never
   /// matches a local path, and a URL that cannot be read matches nothing.
   /// </summary>
   public static bool IsSameRepository(string expected, string actual)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(expected);
      if(string.IsNullOrWhiteSpace(actual))
      {
         return false;
      }

      Location expectedLocation = Parse(expected);
      Location actualLocation = Parse(actual);
      if(expectedLocation.Kind != actualLocation.Kind || expectedLocation.Kind == LocationKind.Unreadable)
      {
         return false;
      }

      if(expectedLocation.Kind == LocationKind.Local)
      {
         return string.Equals(
             expectedLocation.Path, actualLocation.Path,
             OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
      }

      return string.Equals(expectedLocation.Host, actualLocation.Host, StringComparison.OrdinalIgnoreCase)
          && string.Equals(expectedLocation.Path, actualLocation.Path, StringComparison.OrdinalIgnoreCase)
          && (expectedLocation.WebPort is null || actualLocation.WebPort is null || expectedLocation.WebPort == actualLocation.WebPort);
   }



   /// <summary>
   /// True when a credential request from a remote whose URL is
   /// <paramref name="requested"/> may be answered with the token of a sync whose listed
   /// clone URL is <paramref name="expected"/>: both must be HTTPS and name the same
   /// host and port. A remote on another host, plain HTTP, and SSH get no token.
   /// </summary>
   public static bool MayReceiveCredentials(string expected, string? requested)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(expected);
      return TryParseHttps(expected, out Uri? expectedUri, out string? expectedHost)
          && TryParseHttps(requested, out Uri? requestedUri, out string? requestedHost)
          && string.Equals(expectedHost, requestedHost, StringComparison.OrdinalIgnoreCase)
          && expectedUri.Port == requestedUri.Port;
   }



   /// <summary>True when <paramref name="url"/> is an absolute HTTPS URL with a readable host.</summary>
   public static bool IsHttps(string? url) => TryParseHttps(url, out _, out _);



   /// <summary>
   /// The host of an HTTPS <paramref name="url"/>, with its port when that is not 443
   /// and with the brackets of an IPv6 literal; null when the URL is not absolute HTTPS.
   /// This is the scope a token may be used in.
   /// </summary>
   public static string? HttpsAuthority(string? url)
   {
      if(!TryParseHttps(url, out Uri? uri, out string? host))
      {
         return null;
      }

      string authority = uri.HostNameType == UriHostNameType.IPv6 ? $"[{host}]" : host;
      return uri.IsDefaultPort ? authority : authority + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
   }



   /// <summary>
   /// <paramref name="url"/> in a form that is safe to show and log: an origin may
   /// carry credentials someone typed into it. A readable network URL is rebuilt from
   /// its scheme, host, port and path, so user information, query and fragment are
   /// gone; anything else loses everything up to its last <c>@</c>.
   /// </summary>
   public static string Redact(string? url)
   {
      if(string.IsNullOrEmpty(url))
      {
         return "";
      }

      string trimmed = url.Trim();
      int scheme = trimmed.IndexOf("://", StringComparison.Ordinal);
      if(scheme > 0 && Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) && !uri.IsFile && TryGetHost(uri, out string? host))
      {
         string authority = uri.HostNameType == UriHostNameType.IPv6 ? $"[{host}]" : host;
         string port = uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
         return $"{uri.Scheme}://{authority}{port}{uri.AbsolutePath}";
      }

      int at = trimmed.LastIndexOf('@');
      if(at < 0)
      {
         return trimmed;
      }

      return scheme > 0 ? string.Concat(trimmed.AsSpan(0, scheme + 3), trimmed.AsSpan(at + 1)) : trimmed[(at + 1)..];
   }



   private static bool TryParseHttps(string? url, [NotNullWhen(true)] out Uri? uri, [NotNullWhen(true)] out string? host)
   {
      host = null;
      return Uri.TryCreate(url, UriKind.Absolute, out uri)
          && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
          && TryGetHost(uri, out host);
   }



   /// <summary>
   /// The host in its ASCII (punycode) form. <see cref="Uri.IdnHost"/> throws for a
   /// host that parses as a URI but is not a valid internationalized name; such a
   /// host is nobody's, not an error.
   /// </summary>
   private static bool TryGetHost(Uri uri, [NotNullWhen(true)] out string? host)
   {
      try
      {
         host = uri.IdnHost;
         return host.Length > 0;
      }
      catch(UriFormatException)
      {
         host = null;
         return false;
      }
   }



   private static Location Parse(string url)
   {
      string trimmed = url.Trim();
      int scheme = trimmed.IndexOf("://", StringComparison.Ordinal);
      if(scheme > 0)
      {
         if(!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
         {
            return new Location(LocationKind.Unreadable, "", "", null);
         }

         if(uri.IsFile)
         {
            return Local(uri.LocalPath);
         }

         bool web = uri.Scheme is "http" or "https";
         return TryGetHost(uri, out string? host)
             ? new Location(LocationKind.Network, host, NormalizePath(Uri.UnescapeDataString(uri.AbsolutePath)), web ? (uri.IsDefaultPort ? 0 : uri.Port) : null)
             : new Location(LocationKind.Unreadable, "", "", null);
      }

      // The scp-like form git accepts for SSH: [user@]host:path. A single letter before
      // the colon is a Windows drive ("C:\src\repo"), not a host, and a separator
      // before the colon means the colon belongs to a local path.
      int colon = trimmed.IndexOf(':', StringComparison.Ordinal);
      if(colon >= 0)
      {
         string host = trimmed[(trimmed.LastIndexOf('@', colon) + 1)..colon];
         string path = trimmed[(colon + 1)..];
         if(host.Length >= 2 && host.IndexOfAny(s_separators) < 0 && path.Length > 0 && path[0] != '\\')
         {
            return new Location(LocationKind.Network, host, NormalizePath(path), null);
         }
      }

      return Local(trimmed);
   }



   private static Location Local(string path)
       => new(LocationKind.Local, "", Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), null);



   private static string NormalizePath(string path)
   {
      string trimmed = path.Trim('/');
      return trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4].TrimEnd('/') : trimmed;
   }



   private enum LocationKind
   {
      Network,
      Local,
      Unreadable,
   }



   /// <summary>Where a git URL points: a host and path, or a local full path.</summary>
   /// <param name="Kind">Network, local, or not readable as either.</param>
   /// <param name="Host">ASCII host of a network URL; empty otherwise.</param>
   /// <param name="Path">Normalized repository path, or the local full path.</param>
   /// <param name="WebPort">Port of an HTTP(S) URL, 0 for the scheme's default (so HTTP and HTTPS on their usual ports agree); null for SSH, scp-like and local.</param>
   private readonly record struct Location(LocationKind Kind, string Host, string Path, int? WebPort);
}
