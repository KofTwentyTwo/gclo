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
   public void WhatCannotBeAServersAddress_IsRefused_WithTheReason(string? typed, string expected)
   {
      Assert.False(GiteaServerUrl.TryNormalize(typed, out Uri? server, out string? reason));

      Assert.Null(server);
      Assert.StartsWith(expected, reason, StringComparison.Ordinal);
   }
}
