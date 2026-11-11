/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine.Tests;


/// <summary>
/// A forge and, where it has one, its server: what may be one, and which listers
/// belong to it.
/// </summary>
public sealed class ForgeEndpointTests
{
   [Fact]
   public void GitHub_IsOneEndpoint_WithoutAServer_AndWithGitHubsListers()
   {
      Assert.True(ForgeEndpoint.TryCreate(ForgeKind.GitHub, null, out ForgeEndpoint? endpoint, out string? reason));

      Assert.Same(ForgeEndpoint.GitHub, endpoint);
      Assert.Null(reason);
      Assert.Equal(ForgeKind.GitHub, endpoint.Kind);
      Assert.Null(endpoint.Server);
      Assert.Equal("GitHub", endpoint.DisplayName);
      Assert.Equal("GitHub", endpoint.ToString());
      Assert.IsType<GitHubRepositoryLister>(endpoint.CreateRepositoryLister());
      Assert.IsType<GitHubOrganizationLister>(endpoint.CreateOrganizationLister());
      Assert.True(ForgeEndpoint.TryCreate(ForgeKind.GitHub, "  ", out ForgeEndpoint? blank, out _));
      Assert.Same(ForgeEndpoint.GitHub, blank);
   }



   [Theory]
   [InlineData("git.example.test", "https://git.example.test/", "git.example.test")]
   [InlineData("https://git.example.test:8443/gitea", "https://git.example.test:8443/gitea/", "git.example.test:8443")]
   public void AGiteaServer_IsAnEndpointAtItsNormalizedAddress_WithGiteasListers(string typed, string address, string shown)
   {
      Assert.True(ForgeEndpoint.TryCreate(ForgeKind.Gitea, typed, out ForgeEndpoint? endpoint, out string? reason));

      Assert.Null(reason);
      Assert.Equal(ForgeKind.Gitea, endpoint.Kind);
      Assert.Equal(address, endpoint.Server!.AbsoluteUri);
      Assert.Equal(shown, endpoint.DisplayName);
      Assert.Equal(shown, endpoint.ToString());
      Assert.IsType<GiteaRepositoryLister>(endpoint.CreateRepositoryLister());
      Assert.IsType<GiteaOrganizationLister>(endpoint.CreateOrganizationLister());
   }



   [Theory]
   [InlineData(ForgeKind.GitHub, "https://github.example.test", "GitHub has no server address to choose: it is always github.com")]
   [InlineData(ForgeKind.Gitea, null, "a Gitea account needs the address of its server")]
   [InlineData(ForgeKind.Gitea, "   ", "a Gitea account needs the address of its server")]
   [InlineData(ForgeKind.Gitea, " http://git.example.test ", "the server address cannot be used: it has to begin with https://")]
   [InlineData(ForgeKind.Gitea, "https://someone:s3cret-token@git.example.test", "the server address cannot be used: it contains a user name or a password; the token is entered separately")]
   [InlineData((ForgeKind)99, null, "'99' is not a kind of forge gclo knows")]
   public void WhatIsNotAnEndpoint_IsRefused_WithTheReason(ForgeKind kind, string? server, string expected)
   {
      Assert.False(ForgeEndpoint.TryCreate(kind, server, out ForgeEndpoint? endpoint, out string? reason));

      Assert.Null(endpoint);
      Assert.Equal(expected, reason);
   }



   [Fact]
   public void Endpoints_AreEqual_WhenTheyAreTheSamePlace()
   {
      Assert.True(ForgeEndpoint.TryCreate(ForgeKind.Gitea, "git.example.test", out ForgeEndpoint? typed, out _));
      Assert.True(ForgeEndpoint.TryCreate(ForgeKind.Gitea, "https://GIT.example.test/", out ForgeEndpoint? spelledOut, out _));
      Assert.True(ForgeEndpoint.TryCreate(ForgeKind.Gitea, "other.example.test", out ForgeEndpoint? other, out _));

      Assert.Equal(typed, spelledOut);
      Assert.True(typed.Equals((object)spelledOut));
      Assert.Equal(typed.GetHashCode(), spelledOut.GetHashCode());
      Assert.NotEqual(typed, other);
      Assert.NotEqual(typed, ForgeEndpoint.GitHub);
      Assert.False(typed.Equals(null));
      object text = "git.example.test";
      Assert.False(typed.Equals(text));
   }
}
