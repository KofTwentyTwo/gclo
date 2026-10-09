/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;


namespace gclo.Engine;


/// <summary>
/// One <see cref="OctokitGateway"/> per token, shared by both listers for the life of
/// the process, so successive org lookups and repository loads ride one warm HTTP
/// connection instead of paying a DNS + TCP + TLS handshake to api.github.com on
/// every interactive action (#30). Octokit clients are thread-safe for concurrent
/// requests with fixed credentials. Bounded: tokens are rotated rarely, so when the
/// cache grows past a handful of entries it is simply emptied.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Holds live Octokit clients; the offline suite injects fake gateways.")]
internal static class OctokitGatewayCache
{
   private const int MaxEntries = 8;

   private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, OctokitGateway> s_gateways =
       new(StringComparer.Ordinal);



   public static IGitHubGateway Get(string token)
   {
      if(s_gateways.Count >= MaxEntries && !s_gateways.ContainsKey(token))
      {
         s_gateways.Clear();
      }
      return s_gateways.GetOrAdd(token, static t => new OctokitGateway(t));
   }
}
