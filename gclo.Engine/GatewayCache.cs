/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Runtime.InteropServices;
using System.Security.Cryptography;


namespace gclo.Engine;


/// <summary>
/// Keeps the few most recently used gateways, one per token, so successive lookups
/// with one token share a client and its warm connection. Bounded: when a new token
/// arrives and the cache is full, the gateway that has gone unused the longest is
/// dropped, and only that one. Entries are keyed by a hash of the token, so the
/// cache itself holds no token; a dropped gateway is not disposed, because a request
/// that started before it was dropped may still be using it, and it is released
/// with its connections once nothing refers to it.
/// </summary>
internal sealed class GatewayCache
{
   private readonly Lock _gate = new();

   private readonly int _capacity;

   private readonly Func<string, IGitHubGateway> _create;

   /// <summary>Most recently used first.</summary>
   private readonly LinkedList<(string Key, IGitHubGateway Gateway)> _entries = new();



   /// <param name="capacity">How many gateways are kept; at least one.</param>
   /// <param name="create">Makes the gateway for a token that has none.</param>
   public GatewayCache(int capacity, Func<string, IGitHubGateway> create)
   {
      ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
      ArgumentNullException.ThrowIfNull(create);
      _capacity = capacity;
      _create = create;
   }



   /// <summary>How many gateways are held right now.</summary>
   public int Count
   {
      get
      {
         lock(_gate)
         {
            return _entries.Count;
         }
      }
   }



   /// <summary>The gateway for <paramref name="token"/>: the one in use for it, or a new one.</summary>
   public IGitHubGateway Get(string token)
   {
      string key = KeyOf(token);
      lock(_gate)
      {
         for(LinkedListNode<(string Key, IGitHubGateway Gateway)>? node = _entries.First; node is not null; node = node.Next)
         {
            if(string.Equals(node.Value.Key, key, StringComparison.Ordinal))
            {
               _entries.Remove(node);
               _entries.AddFirst(node);
               return node.Value.Gateway;
            }
         }

         IGitHubGateway gateway = _create(token);
         _entries.AddFirst((key, gateway));
         if(_entries.Count > _capacity)
         {
            _entries.RemoveLast();
         }
         return gateway;
      }
   }



   /// <summary>
   /// What an entry is filed under: a SHA-256 of the token, never the token. Taken
   /// over the string exactly as it is (its UTF-16 code units), so two different
   /// strings never share a key by being encoded to the same bytes.
   /// </summary>
   internal static string KeyOf(string token)
   {
      ArgumentNullException.ThrowIfNull(token);
      return Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(token.AsSpan())));
   }
}
