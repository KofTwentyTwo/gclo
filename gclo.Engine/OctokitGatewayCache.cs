/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;


namespace gclo.Engine;


/// <summary>
/// One <see cref="OctokitGateway"/> per token, shared by both listers, so successive
/// org lookups and repository loads ride one warm HTTP connection instead of paying
/// a DNS + TCP + TLS handshake to api.github.com on every interactive action (#30).
/// Octokit clients are thread-safe for concurrent requests with fixed credentials.
/// Which gateways are kept, and for how long, is <see cref="GatewayCache"/>'s rule.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Holds live Octokit clients; the offline suite injects fake gateways.")]
internal static class OctokitGatewayCache
{
   // One per saved account that has a token of its own, for a handful of accounts.
   private const int MaxEntries = 8;

   private static readonly GatewayCache s_gateways = new(MaxEntries, static token => new OctokitGateway(token));



   public static IGitHubGateway Get(string token) => s_gateways.Get(token);
}
