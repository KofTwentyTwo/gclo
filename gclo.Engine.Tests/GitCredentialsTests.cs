/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using LibGit2Sharp;
using LibGit2Sharp.Handlers;


namespace gclo.Engine.Tests;


/// <summary>
/// The credential callback <see cref="LibGit2GitClient"/> hands to libgit2 (#139). The
/// offline fixtures are local paths, which never ask for credentials, so the callback
/// is called here the way libgit2 calls it: with the URL of the remote that asks.
/// </summary>
public sealed class GitCredentialsTests
{
   private const string Token = "ghp_secret_token_value_1234567890";

   private const string Listed = "https://github.com/acme/repo.git";



   [Theory]
   [InlineData("https://github.com/acme/repo.git")]
   [InlineData("https://GitHub.com/acme/repo.git")]
   public void ListedHostOverHttps_GetsTheTokenAsThePassword(string asking)
   {
      CredentialsHandler provider = LibGit2GitClient.MakeCredentialsProvider(Token, Listed);

      UsernamePasswordCredentials credentials = Assert.IsType<UsernamePasswordCredentials>(provider(asking, null, SupportedCredentialTypes.UsernamePassword));

      Assert.Equal("x-access-token", credentials.Username);
      Assert.Equal(Token, credentials.Password);
   }



   [Theory]
   [InlineData("https://evil.example/acme/repo.git")]
   [InlineData("https://github.com.evil.example/acme/repo.git")]
   [InlineData("https://github.com:8443/acme/repo.git")]
   [InlineData("http://github.com/acme/repo.git")]
   [InlineData("ssh://git@github.com/acme/repo.git")]
   [InlineData("git@github.com:acme/repo.git")]
   public void AnyOtherRemote_GetsNothing_AndTheErrorDoesNotCarryTheToken(string asking)
   {
      CredentialsHandler provider = LibGit2GitClient.MakeCredentialsProvider(Token, Listed);

      InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
          () => provider(asking, null, SupportedCredentialTypes.UsernamePassword));

      Assert.Contains("Nothing was sent", ex.Message);
      Assert.DoesNotContain(Token, ex.Message);
   }



   [Fact]
   public void ListedUrlThatIsNotHttps_NobodyGetsTheToken()
   {
      CredentialsHandler provider = LibGit2GitClient.MakeCredentialsProvider(Token, "http://git.example.com/acme/repo.git");

      Assert.Throws<InvalidOperationException>(
          () => provider("http://git.example.com/acme/repo.git", null, SupportedCredentialTypes.UsernamePassword));
   }



   [Fact]
   public void ErrorForAnotherRemote_DoesNotShowCredentialsInItsUrl()
   {
      CredentialsHandler provider = LibGit2GitClient.MakeCredentialsProvider(Token, Listed);

      InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
          () => provider("https://someone:hunter2@evil.example/acme/repo.git", null, SupportedCredentialTypes.UsernamePassword));

      Assert.Contains("https://evil.example/acme/repo.git", ex.Message);
      Assert.DoesNotContain("hunter2", ex.Message);
   }



   [Fact]
   public void ThirdRequest_MeansTheTokenWasRejected_NotAnEndlessReplay()
   {
      CredentialsHandler provider = LibGit2GitClient.MakeCredentialsProvider(Token, Listed);
      provider(Listed, null, SupportedCredentialTypes.UsernamePassword);
      provider(Listed, null, SupportedCredentialTypes.UsernamePassword);

      InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
          () => provider(Listed, null, SupportedCredentialTypes.UsernamePassword));

      Assert.Contains("rejected the token", ex.Message);
      Assert.DoesNotContain(Token, ex.Message);
   }



   [Fact]
   public void RefusedRequests_DoNotCountTowardTheReplayLimit()
   {
      CredentialsHandler provider = LibGit2GitClient.MakeCredentialsProvider(Token, Listed);
      for(int i = 0; i < 5; i++)
      {
         Assert.Throws<InvalidOperationException>(
             () => provider("https://evil.example/x.git", null, SupportedCredentialTypes.UsernamePassword));
      }

      Assert.IsType<UsernamePasswordCredentials>(provider(Listed, null, SupportedCredentialTypes.UsernamePassword));
   }
}
