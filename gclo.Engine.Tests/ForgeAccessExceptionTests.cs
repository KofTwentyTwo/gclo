/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine.Tests;


/// <summary>
/// One failure type for every forge: code that handles <see cref="ForgeAccessException"/>
/// handles the GitHub listers' failures too, and code written against
/// <see cref="GitHubAccessException"/> before there was a second forge keeps working.
/// </summary>
public sealed class ForgeAccessExceptionTests
{
   [Fact]
   public void TheKindsOfBothTypes_AreTheSame_NameForNameAndValueForValue()
   {
      Assert.Equal(Enum.GetNames<GitHubAccessKind>(), Enum.GetNames<ForgeAccessKind>(), StringComparer.Ordinal);
      Assert.Equal(
          Enum.GetValues<GitHubAccessKind>().Select(kind => (int)kind),
          Enum.GetValues<ForgeAccessKind>().Select(kind => (int)kind));
   }



   [Theory]
   [InlineData(GitHubAccessKind.Unauthorized, ForgeAccessKind.Unauthorized)]
   [InlineData(GitHubAccessKind.Forbidden, ForgeAccessKind.Forbidden)]
   [InlineData(GitHubAccessKind.RateLimited, ForgeAccessKind.RateLimited)]
   [InlineData(GitHubAccessKind.NotFound, ForgeAccessKind.NotFound)]
   [InlineData(GitHubAccessKind.Unavailable, ForgeAccessKind.Unavailable)]
   public void AGitHubFailure_IsAForgeFailureOfTheSameKind(GitHubAccessKind gitHub, ForgeAccessKind forge)
   {
      var cause = new TimeoutException("cause");
      var failure = new GitHubAccessException(gitHub, "what happened", cause);

      ForgeAccessException asForge = failure;
      InvalidOperationException asBefore = failure;

      Assert.Equal(gitHub, failure.Kind);
      Assert.Equal(forge, asForge.Kind);
      Assert.Equal("what happened", asBefore.Message);
      Assert.Same(cause, asBefore.InnerException);
   }



   [Fact]
   public void AForgeFailure_CarriesItsKind_ItsMessage_AndItsCause()
   {
      var cause = new TimeoutException("cause");

      var failure = new ForgeAccessException(ForgeAccessKind.RateLimited, "slow down", cause);

      Assert.Equal(ForgeAccessKind.RateLimited, failure.Kind);
      Assert.Equal("slow down", failure.Message);
      Assert.Same(cause, failure.InnerException);
      Assert.Null(new ForgeAccessException(ForgeAccessKind.NotFound, "nothing there").InnerException);
   }
}
