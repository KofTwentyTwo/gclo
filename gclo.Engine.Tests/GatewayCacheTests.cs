/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine.Tests;


/// <summary>
/// <see cref="GatewayCache"/> decides which API clients are kept: one per token, a
/// small fixed number of them, the longest unused dropped first, and no token held
/// by the cache itself (#151).
/// </summary>
public sealed class GatewayCacheTests
{
   /// <summary>A gateway that only has an identity; the cache never calls it.</summary>
   private sealed class Gateway(string token) : IGitHubGateway
   {
      public string Token { get; } = token;

      public Task<IReadOnlyList<GitHubRepo>> GetOrganizationRepositoriesPageAsync(string organization, int page)
          => throw new NotSupportedException();

      public Task<IReadOnlyList<GitHubRepo>> GetOwnRepositoriesPageAsync(int page) => throw new NotSupportedException();

      public Task<IReadOnlyList<GitHubRepo>> GetUserRepositoriesPageAsync(string user, int page)
          => throw new NotSupportedException();

      public Task<string> GetCurrentUserLoginAsync() => throw new NotSupportedException();

      public Task<IReadOnlyList<string>> GetOrganizationLoginsAsync() => throw new NotSupportedException();

      public Task<GitHubAccountKind> GetAccountKindAsync(string login) => throw new NotSupportedException();
   }



   private readonly List<string> _created = [];



   private GatewayCache NewCache(int capacity)
       => new(capacity, token =>
       {
          lock(_created)
          {
             _created.Add(token);
          }
          return new Gateway(token);
       });



   [Fact]
   public void Get_TheSameToken_GivesTheSameGateway_MadeOnce()
   {
      GatewayCache cache = NewCache(capacity: 2);

      IGitHubGateway first = cache.Get("token-a");
      IGitHubGateway second = cache.Get("token-a");

      Assert.Same(first, second);
      Assert.Equal(["token-a"], _created, StringComparer.Ordinal);
      Assert.Equal(1, cache.Count);
   }



   [Fact]
   public void Get_AnotherToken_GivesAnotherGateway_MadeForThatToken()
   {
      GatewayCache cache = NewCache(capacity: 2);

      var a = (Gateway)cache.Get("token-a");
      var b = (Gateway)cache.Get("token-b");

      Assert.NotSame(a, b);
      Assert.Equal("token-a", a.Token);
      Assert.Equal("token-b", b.Token);
      Assert.Equal(2, cache.Count);
   }



   [Fact]
   public void Get_WhenFull_DropsOnlyTheGatewayUnusedTheLongest()
   {
      GatewayCache cache = NewCache(capacity: 2);
      IGitHubGateway a = cache.Get("token-a");
      IGitHubGateway b = cache.Get("token-b");
      Assert.Same(a, cache.Get("token-a")); // a is now the more recently used of the two

      cache.Get("token-c"); // full: b goes, a stays

      Assert.Equal(2, cache.Count);
      Assert.Same(a, cache.Get("token-a"));
      Assert.NotSame(b, cache.Get("token-b")); // made again
      Assert.Equal(["token-a", "token-b", "token-c", "token-b"], _created, StringComparer.Ordinal);
   }



   [Fact]
   public void Get_NeverHoldsMoreThanItsCapacity()
   {
      GatewayCache cache = NewCache(capacity: 3);

      for(int i = 0; i < 50; i++)
      {
         cache.Get($"token-{i}");
      }

      Assert.Equal(3, cache.Count);
   }



   [Fact]
   public void Get_FromManyThreads_MakesOneGatewayPerToken()
   {
      GatewayCache cache = NewCache(capacity: 4);
      var seen = new System.Collections.Concurrent.ConcurrentBag<IGitHubGateway>();

      Parallel.For(0, 200, i => seen.Add(cache.Get($"token-{i % 4}")));

      Assert.Equal(4, seen.Distinct().Count());
      Assert.Equal(4, _created.Count);
   }



   [Fact]
   public void KeyOf_IsAHashOfTheToken_NotTheToken()
   {
      string key = GatewayCache.KeyOf("ghp_exampleTokenValue");

      Assert.Equal(64, key.Length); // SHA-256, as hexadecimal
      Assert.DoesNotContain("ghp_exampleTokenValue", key, StringComparison.OrdinalIgnoreCase);
      Assert.Equal(key, GatewayCache.KeyOf("ghp_exampleTokenValue"));
      Assert.False(string.Equals(key, GatewayCache.KeyOf("ghp_exampleTokenValuf"), StringComparison.Ordinal));
   }



   [Fact]
   public void KeyOf_TwoStringsThatEncodeToTheSameBytes_StillHaveDifferentKeys()
   {
      // Half of a surrogate pair cannot be encoded; an encoder turns each into the same replacement.
      string one = "ghp_" + (char)0xD800 + "abc";
      string other = "ghp_" + (char)0xD801 + "abc";

      Assert.False(string.Equals(GatewayCache.KeyOf(one), GatewayCache.KeyOf(other), StringComparison.Ordinal));
   }



   [Fact]
   public void Constructor_RefusesACapacityBelowOne_AndAMissingFactory()
   {
      Assert.Throws<ArgumentOutOfRangeException>(() => new GatewayCache(0, token => new Gateway(token)));
      Assert.Throws<ArgumentNullException>(() => new GatewayCache(1, null!));
      Assert.Throws<ArgumentNullException>(() => GatewayCache.KeyOf(null!));
   }
}
