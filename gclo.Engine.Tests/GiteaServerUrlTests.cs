/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine.Tests;


/// <summary>
/// What may be the address of a Gitea server: the place an account's token is sent
/// to and its repositories are cloned from.
/// </summary>
public sealed class GiteaServerUrlTests
{
   [Theory]
   [InlineData("git.example.test", "https://git.example.test/")] // typed without a scheme: https
   [InlineData("https://git.example.test", "https://git.example.test/")]
   [InlineData("  https://git.example.test/  ", "https://git.example.test/")]
   [InlineData("HTTPS://Git.Example.TEST", "https://git.example.test/")]
   [InlineData("https://git.example.test:443/", "https://git.example.test/")]
   [InlineData("https://git.example.test:8443", "https://git.example.test:8443/")]
   [InlineData("git.example.test:8443/gitea", "https://git.example.test:8443/gitea/")]
   [InlineData("https://example.test/gitea", "https://example.test/gitea/")] // a server under a path
   [InlineData("https://example.test/gitea/", "https://example.test/gitea/")]
   [InlineData("https://bücher.example.test", "https://xn--bcher-kva.example.test/")]
   [InlineData("https://10.1.2.3:3000", "https://10.1.2.3:3000/")] // a server on the local network is the usual case
   [InlineData("localhost:3000/gitea", "https://localhost:3000/gitea/")]
   [InlineData("[2001:db8::1]", "https://[2001:db8::1]/")]
   [InlineData("[2001:db8::1]:3000", "https://[2001:db8::1]:3000/")]
   public void AnAddress_BecomesTheServersBaseAddress(string typed, string expected)
   {
      Assert.True(GiteaServerUrl.TryNormalize(typed, out Uri? server, out string? reason));

      Assert.Equal(expected, server.AbsoluteUri);
      Assert.Null(reason);
   }



   [Theory]
   [InlineData(null, "it is empty")]
   [InlineData("", "it is empty")]
   [InlineData("   ", "it is empty")]
   [InlineData("http://git.example.test", "it has to begin with https://")] // the token never travels unencrypted
   [InlineData("ftp://git.example.test", "it has to begin with https://")]
   [InlineData("ssh://git@git.example.test", "it has to begin with https://")]
   [InlineData("https://user@git.example.test", "it contains a user name or a password")]
   [InlineData("https://user:secret@git.example.test", "it contains a user name or a password")]
   [InlineData("https://git.example.test/?next=1", "it has a '?' or a '#' part")]
   [InlineData("https://git.example.test/#top", "it has a '?' or a '#' part")]
   [InlineData("git example test", "it contains a space or a control character")]
   [InlineData("https://git.example.test/\tx", "it contains a space or a control character")]
   [InlineData("https://git.example.test/\u0007", "it contains a space or a control character")]
   [InlineData("https://", "it is not an address")]
   [InlineData("://nothing", "it is not an address")]
   [InlineData("https://[not-an-address", "it is not an address")]
   [InlineData("https:/git.example.test", "it is not an address (a server's address looks like")] // one slash: "https" must not become the host
   [InlineData("http:/git.example.test", "it is not an address (a server's address looks like")]
   [InlineData("http:git.example.test", "it is not an address (a server's address looks like")]
   [InlineData("git.example.test:", "it is not an address (a server's address looks like")]
   [InlineData("git.example.test:port", "it is not an address (a server's address looks like")]
   [InlineData("git.example.test:123456/gitea", "it is not an address (a server's address looks like")]
   [InlineData("[2001:db8::1]:x", "it is not an address (a server's address looks like")]
   [InlineData("https://exa\u00B4mple.test", "it is not an address")] // a character that has no place in a host name
   public void WhatCannotBeAServersAddress_IsRefused_WithTheReason(string? typed, string expected)
   {
      Assert.False(GiteaServerUrl.TryNormalize(typed, out Uri? server, out string? reason));

      Assert.Null(server);
      Assert.StartsWith(expected, reason, StringComparison.Ordinal);
   }



   [Fact]
   public void WhateverIsTyped_ItIsAnsweredWithYesOrNo_NeverWithAnException()
   {
      // Every character of the first plane once in a host, and a few thousand random strings.
      var random = new Random(20261111);
      for(int c = 1; c < 0xD800; c++)
      {
         _ = GiteaServerUrl.TryNormalize($"https://exa{(char)c}mple.test/", out _, out _);
         _ = GiteaServerUrl.TryNormalize($"exa{(char)c}mple.test", out _, out _);
      }
      for(int i = 0; i < 4000; i++)
      {
         string text = new([.. Enumerable.Range(0, random.Next(1, 24)).Select(_ => (char)random.Next(0x20, 0x180))]);
         if(GiteaServerUrl.TryNormalize(text, out Uri? server, out string? reason))
         {
            Assert.Equal(Uri.UriSchemeHttps, server.Scheme);
            Assert.Equal("", server.UserInfo);
         }
         else
         {
            Assert.False(string.IsNullOrEmpty(reason));
         }
      }
   }
}
