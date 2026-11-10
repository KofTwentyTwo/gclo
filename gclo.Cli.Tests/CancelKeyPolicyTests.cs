/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Cli.Tests;


/// <summary>
/// The first Ctrl+C is held back for the commands that have something to finish
/// (a run that stops gracefully, a change to the accounts that must not be cut off
/// between its two stores), not while waiting at a prompt, and not twice (#159).
/// </summary>
public sealed class CancelKeyPolicyTests
{
   [Theory]
   [InlineData("sync")]
   [InlineData("repos")]
   [InlineData("orgs")]
   [InlineData("accounts")]
   public void ACommandWithSomethingToFinish_IsNotCutOff(string command)
       => Assert.True(CancelKeyPolicy.HoldsBack(command, isReadingInput: false, alreadyRequested: false));



   [Theory]
   [InlineData("help")]
   [InlineData("--version")]
   [InlineData("no-such-command")]
   [InlineData(null)]
   public void AnythingElse_EndsAtOnce(string? command)
       => Assert.False(CancelKeyPolicy.HoldsBack(command, isReadingInput: false, alreadyRequested: false));



   [Theory]
   [InlineData("sync")]
   [InlineData("accounts")]
   public void WhileWaitingForALineOfInput_TheProcessEndsAtOnce(string command)
       => Assert.False(CancelKeyPolicy.HoldsBack(command, isReadingInput: true, alreadyRequested: false));



   [Theory]
   [InlineData("sync")]
   [InlineData("accounts")]
   public void ASecondCtrlC_EndsTheProcess(string command)
       => Assert.False(CancelKeyPolicy.HoldsBack(command, isReadingInput: false, alreadyRequested: true));



   [Fact]
   public void TheNotice_SaysWhatIsHappening_WhichForAnAccountsChangeIsNotACancellation()
   {
      Assert.StartsWith("Canceling...", CancelKeyPolicy.Notice("sync"));
      Assert.StartsWith("Canceling...", CancelKeyPolicy.Notice(null));
      Assert.StartsWith("Finishing the change to the accounts...", CancelKeyPolicy.Notice("accounts"));
   }
}
