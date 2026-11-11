/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.ViewModels;


namespace gclo.Engine.Tests;


/// <summary>
/// <see cref="ShareInvitation"/> writes the email behind Help &gt; Share: what the app
/// is, where to get it, and a link a mail program opens with everything but the
/// recipient filled in (#163).
/// </summary>
public sealed class ShareInvitationTests
{
   private const string Repository = "https://github.com/KofTwentyTwo/gclo";

   private static ShareInvitation Invitation() => ShareInvitation.Create(Repository, "1.2.3 (abc1234)");



   [Fact]
   public void TheBody_SaysWhatItIs_WhereToGetIt_AndHowToInstallIt()
   {
      ShareInvitation invitation = Invitation();

      Assert.Equal("Try gclo: clone and update every repository of a GitHub organization", invitation.Subject);
      Assert.Contains("clones every repository of a GitHub organization or account", invitation.Body);
      Assert.Contains(Repository + "/releases/latest", invitation.Body);
      Assert.Contains("gclo-stable-Setup.exe", invitation.Body);
      Assert.Contains("scoop bucket add gclo " + Repository, invitation.Body);
      Assert.Contains("scoop install gclo", invitation.Body);
      Assert.Contains(Repository + "#readme", invitation.Body);
      // Filing an issue needs an account on the project's server; the message does
      // not send a stranger to a sign-in page. They answer the person who wrote.
      Assert.DoesNotContain("/issues", invitation.Body);
      Assert.Contains("tell me what you think", invitation.Body);
      Assert.EndsWith("(sent from gclo 1.2.3 (abc1234))", invitation.Body);
   }



   [Fact]
   public void TheLink_OpensANewMessage_WithNoRecipient_AndCarriesSubjectAndBodyExactly()
   {
      ShareInvitation invitation = Invitation();
      string link = invitation.MailtoUri.OriginalString;

      Assert.Equal("mailto", invitation.MailtoUri.Scheme);
      Assert.StartsWith("mailto:?subject=", link); // nothing between "mailto:" and "?": the user types the address
      string[] parts = link["mailto:?".Length..].Split('&');
      Assert.Equal(2, parts.Length);
      Assert.Equal(invitation.Subject, Uri.UnescapeDataString(parts[0]["subject=".Length..]));
      Assert.Equal(invitation.Body, Uri.UnescapeDataString(parts[1]["body=".Length..]));
   }



   [Fact]
   public void TheLink_IsShortEnoughForMailProgramsThatCutLongOnes_AndHasNoRawSpaceOrLineBreak()
   {
      string link = Invitation().MailtoUri.OriginalString;

      Assert.True(link.Length < ShareInvitation.MaxLinkLength, $"the link is {link.Length} characters long");
      Assert.True(Invitation().FitsInALink);
      Assert.DoesNotContain(' ', link);
      Assert.DoesNotContain('\n', link);
      Assert.DoesNotContain('\r', link);
      Assert.Contains("%0D%0A", link, StringComparison.Ordinal); // line ends travel as CR LF
   }



   [Fact]
   public void TheBody_IsPlainText_WithCrLfLineEnds_AndNoLineThatAMailProgramWouldMangle()
   {
      string body = Invitation().Body;

      Assert.DoesNotContain("\n", body.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
      Assert.All(body.Split("\r\n"), line => Assert.Equal(line.TrimEnd(), line));
   }



   [Fact]
   public void AsPlainText_IsWhatAPersonCanPasteIntoAMessage()
   {
      ShareInvitation invitation = Invitation();

      Assert.Equal("Subject: " + invitation.Subject + "\r\n\r\n" + invitation.Body, invitation.AsPlainText);
   }



   [Fact]
   public void ATrailingSlashOnTheRepositoryAddress_DoesNotDoubleUpInTheLinks()
   {
      var invitation = ShareInvitation.Create(Repository + "/", " 1.2.3 ");

      Assert.Contains(Repository + "/releases/latest", invitation.Body);
      Assert.DoesNotContain("gclo//", invitation.Body);
      Assert.EndsWith("(sent from gclo 1.2.3)", invitation.Body);
   }



   [Fact]
   public void SpaceAroundTheAddress_IsNotPartOfTheLinks()
   {
      var invitation = ShareInvitation.Create("  " + Repository + "  ", "1.2.3");

      Assert.Contains("\r\n" + Repository + "/releases/latest\r\n", invitation.Body);
   }



   [Fact]
   public void AnInvitationTooLongForALink_SaysSo()
   {
      var invitation = ShareInvitation.Create("https://example.test/" + new string('a', 1200), "1.2.3");

      Assert.True(invitation.MailtoUri.OriginalString.Length > ShareInvitation.MaxLinkLength);
      Assert.False(invitation.FitsInALink);
   }



   [Theory]
   [InlineData("")]
   [InlineData("   ")]
   [InlineData("not a web address")]
   [InlineData("ftp://example.test/gclo")]
   [InlineData("/relative/path")]
   [InlineData("https://someone:secret@example.test/gclo")] // a name and a password would travel in every link
   [InlineData("https://example.test/gclo?tab=readme")]
   [InlineData("https://example.test/gclo#readme")]
   public void AnAddressThatIsNotAPlainWebAddress_IsRefused(string repositoryUrl)
       => Assert.ThrowsAny<ArgumentException>(() => ShareInvitation.Create(repositoryUrl, "1.2.3"));



   [Theory]
   [InlineData("1.2.3\r\nBcc: someone@example.test")]
   [InlineData("1.2.3\nmore")]
   [InlineData("1.2.3\t(x)")]
   public void AVersionWithAControlCharacter_IsRefused(string version)
       => Assert.ThrowsAny<ArgumentException>(() => ShareInvitation.Create(Repository, version));



   [Fact]
   public void AMissingVersion_IsRefused()
   {
      Assert.ThrowsAny<ArgumentException>(() => ShareInvitation.Create(Repository, " "));
      Assert.ThrowsAny<ArgumentException>(() => ShareInvitation.Create(Repository, null!));
      Assert.ThrowsAny<ArgumentException>(() => ShareInvitation.Create(null!, "1.2.3"));
   }
}
