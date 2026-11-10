/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine.Tests;


/// <summary>
/// Pins the two decisions <see cref="RemoteUrl"/> makes for a sync (#139): whether a
/// folder's origin is the listed repository, and whether a host may be given the token.
/// </summary>
public sealed class RemoteUrlTests
{
   private const string Listed = "https://github.com/acme/repo.git";

   // ---------------------------------------------------------------- same repository

   [Theory]
   [InlineData("https://github.com/acme/repo.git")]
   [InlineData("https://github.com/acme/repo")]
   [InlineData("https://github.com/acme/repo/")]
   [InlineData("https://github.com/acme/repo.git/")]
   [InlineData("https://GitHub.com/Acme/Repo.GIT")]
   [InlineData("  https://github.com/acme/repo.git  ")]
   [InlineData("https://someone:secret@github.com/acme/repo.git")]
   [InlineData("http://github.com/acme/repo.git")]
   [InlineData("ssh://git@github.com/acme/repo.git")]
   [InlineData("ssh://git@github.com:22/acme/repo.git")]
   [InlineData("git@github.com:acme/repo.git")]
   [InlineData("github.com:acme/repo")]
   public void IsSameRepository_SameHostAndPath_HoweverTheOriginIsSpelled(string origin)
   {
      Assert.True(RemoteUrl.IsSameRepository(Listed, origin));
   }



   [Theory]
   [InlineData("https://github.com/acme/other.git")]
   [InlineData("https://github.com/someone-else/repo.git")]
   [InlineData("https://github.com/acme/repo.git/extra")]
   [InlineData("https://github.example.com/acme/repo.git")]
   [InlineData("https://gitlab.com/acme/repo.git")]
   [InlineData("https://github.com:8443/acme/repo.git")]
   [InlineData("https://\u00fc-/acme/repo.git")]
   [InlineData("git@bitbucket.org:acme/repo.git")]
   [InlineData("ssh://git@old-host.example/acme/repo.git")]
   [InlineData(@"C:\src\acme\repo")]
   [InlineData("/srv/git/acme/repo.git")]
   [InlineData("file:///srv/git/acme/repo.git")]
   [InlineData("https://")]
   [InlineData("")]
   [InlineData("   ")]
   public void IsSameRepository_AnotherRepository_OrNoRemoteAtAll_IsNotTheListedOne(string origin)
   {
      Assert.False(RemoteUrl.IsSameRepository(Listed, origin));
   }



   [Fact]
   public void IsSameRepository_LocalPaths_CompareAsFullPaths()
   {
      string folder = Path.Combine(Path.GetTempPath(), "gclo-tests", "remote-url", "source");
      string other = Path.Combine(Path.GetTempPath(), "gclo-tests", "remote-url", "another");

      Assert.True(RemoteUrl.IsSameRepository(folder, folder));
      Assert.True(RemoteUrl.IsSameRepository(folder, folder + Path.DirectorySeparatorChar));
      Assert.True(RemoteUrl.IsSameRepository(folder, new Uri(folder).AbsoluteUri));
      Assert.True(RemoteUrl.IsSameRepository(folder, Path.Combine(folder, "..", "source")));
      Assert.False(RemoteUrl.IsSameRepository(folder, other));
      // A listed local path is never satisfied by a network origin.
      Assert.False(RemoteUrl.IsSameRepository(folder, Listed));
   }



   [Theory]
   [InlineData("")]
   [InlineData("  ")]
   public void IsSameRepository_BlankExpectedUrl_IsACallerError(string expected)
   {
      Assert.Throws<ArgumentException>(() => RemoteUrl.IsSameRepository(expected, Listed));
   }



   // ---------------------------------------------------------------- credentials

   [Theory]
   [InlineData("https://github.com/acme/repo.git")]
   [InlineData("https://github.com/acme/repo.git/info/refs?service=git-upload-pack")]
   [InlineData("https://GITHUB.com/another/path")]
   [InlineData("https://github.com:443/acme/repo.git")]
   public void MayReceiveCredentials_TheListedHostOverHttps_Yes(string requested)
   {
      Assert.True(RemoteUrl.MayReceiveCredentials(Listed, requested));
   }



   [Theory]
   [InlineData("https://evil.example/acme/repo.git")]
   [InlineData("https://github.com.evil.example/acme/repo.git")]
   [InlineData("https://github.com@evil.example/acme/repo.git")]
   [InlineData("https://\u00fc-/acme/repo.git")]
   [InlineData("https://github.com:8443/acme/repo.git")]
   [InlineData("http://github.com/acme/repo.git")]
   [InlineData("ssh://git@github.com/acme/repo.git")]
   [InlineData("git@github.com:acme/repo.git")]
   [InlineData("github.com/acme/repo.git")]
   [InlineData("")]
   [InlineData(null)]
   public void MayReceiveCredentials_AnyOtherHostPortOrScheme_No(string? requested)
   {
      Assert.False(RemoteUrl.MayReceiveCredentials(Listed, requested));
   }



   [Theory]
   [InlineData("http://github.com/acme/repo.git")]
   [InlineData(@"C:\src\acme\repo")]
   [InlineData("ssh://git@github.com/acme/repo.git")]
   public void MayReceiveCredentials_ListedUrlIsNotHttps_NobodyGetsTheToken(string listed)
   {
      Assert.False(RemoteUrl.MayReceiveCredentials(listed, listed));
      Assert.False(RemoteUrl.MayReceiveCredentials(listed, "https://github.com/acme/repo.git"));
   }



   [Fact]
   public void MayReceiveCredentials_BlankExpectedUrl_IsACallerError()
   {
      Assert.Throws<ArgumentException>(() => RemoteUrl.MayReceiveCredentials(" ", Listed));
   }



   // ---------------------------------------------------------------- authority

   [Theory]
   [InlineData("https://github.com/acme/repo.git", "github.com")]
   [InlineData("https://GitHub.com:443/acme/repo.git", "github.com")]
   [InlineData("https://git.example.com:3000/acme/repo.git", "git.example.com:3000")]
   [InlineData("https://[::1]:3000/acme/repo.git", "[::1]:3000")]
   [InlineData("https://\u00fc-/acme/repo.git", null)]
   [InlineData("http://github.com/acme/repo.git", null)]
   [InlineData("git@github.com:acme/repo.git", null)]
   [InlineData("", null)]
   [InlineData(null, null)]
   public void HttpsAuthority_IsTheHostATokenIsScopedTo_OrNullWithoutHttps(string? url, string? expected)
   {
      Assert.Equal(expected, RemoteUrl.HttpsAuthority(url));
   }



   // ---------------------------------------------------------------- redaction

   [Theory]
   [InlineData("https://someone:hunter2@github.com/acme/repo.git", "https://github.com/acme/repo.git")]
   [InlineData("https://token@github.com/acme/repo.git", "https://github.com/acme/repo.git")]
   [InlineData("  https://someone:hunter2@github.com/acme/repo.git  ", "https://github.com/acme/repo.git")]
   [InlineData("https://someone:hun@ter2@github.com/acme/repo.git", "https://github.com/acme/repo.git")]
   [InlineData("https://someone:hun/ter2@github.com/acme/repo.git", "https://github.com/acme/repo.git")]
   [InlineData("https://github.com/acme/repo.git?access_token=hunter2", "https://github.com/acme/repo.git")]
   [InlineData("https://github.com/acme/repo.git#hunter2", "https://github.com/acme/repo.git")]
   [InlineData("https://someone:hunter2@git.example.com:3000/acme/repo.git", "https://git.example.com:3000/acme/repo.git")]
   [InlineData("https://someone:hunter2@[::1]:3000/acme/repo.git", "https://[::1]:3000/acme/repo.git")]
   [InlineData("ssh://git@github.com", "ssh://github.com/")]
   [InlineData("https://github.com/acme/repo.git", "https://github.com/acme/repo.git")]
   [InlineData("https://github.com/acme/user@example.git", "https://github.com/acme/user@example.git")]
   [InlineData("https://github.com", "https://github.com/")]
   [InlineData("https://", "https://")]
   [InlineData(@"C:\src\acme\repo", @"C:\src\acme\repo")]
   [InlineData("git@github.com:acme/repo.git", "github.com:acme/repo.git")]
   [InlineData("someone:hunter2@github.com:acme/repo.git", "github.com:acme/repo.git")]
   [InlineData("", "")]
   [InlineData(null, "")]
   public void Redact_NeverShowsUserInformationQueryOrFragment(string? url, string expected)
   {
      string redacted = RemoteUrl.Redact(url);

      Assert.Equal(expected, redacted);
      Assert.DoesNotContain("hunter2", redacted);
      Assert.DoesNotContain("ter2", redacted);
   }



   [Theory]
   [InlineData("https://github.com/acme/repo.git", true)]
   [InlineData("HTTPS://github.com", true)]
   [InlineData("http://github.com/acme/repo.git", false)]
   [InlineData("ssh://git@github.com/acme/repo.git", false)]
   [InlineData("git@github.com:acme/repo.git", false)]
   [InlineData(@"C:\src\acme\repo", false)]
   [InlineData("https://\u00fc-/acme/repo.git", false)]
   [InlineData(null, false)]
   public void IsHttps_OnlyAnAbsoluteHttpsUrlWithAReadableHost(string? url, bool expected)
   {
      Assert.Equal(expected, RemoteUrl.IsHttps(url));
   }
}
