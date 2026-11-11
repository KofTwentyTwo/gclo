/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.ViewModels;


/// <summary>
/// An invitation to try the app, written as an email that the user's own mail
/// program opens ready to send: a subject, a plain-text body that says what the app
/// is, where to download the latest release, and how to install it, and the
/// <c>mailto:</c> link that carries both. The link names no recipient, so the user
/// types an address and sends. Nothing is sent by the app, and nothing about the
/// user, their accounts, or their repositories is in the text.
/// </summary>
public sealed class ShareInvitation
{
   /// <summary>
   /// The longest <c>mailto:</c> link worth handing to a mail program: some of them
   /// cut a longer one short. The text is written to stay well below this; see
   /// <see cref="FitsInALink"/> for an invitation that does not.
   /// </summary>
   public const int MaxLinkLength = 2000;



   private ShareInvitation(string subject, string body)
   {
      Subject = subject;
      Body = body;
      MailtoUri = new Uri("mailto:?subject=" + Uri.EscapeDataString(subject) + "&body=" + Uri.EscapeDataString(body));
   }



   /// <summary>The subject line.</summary>
   public string Subject { get; }

   /// <summary>The message, as plain text with CR LF line ends.</summary>
   public string Body { get; }

   /// <summary>The link that opens a new message with the subject and the body filled in, and no recipient.</summary>
   public Uri MailtoUri { get; }

   /// <summary>Subject and body as one text, to show and copy when no mail program takes the link.</summary>
   public string AsPlainText => "Subject: " + Subject + "\r\n\r\n" + Body;

   /// <summary>
   /// False when <see cref="MailtoUri"/> is longer than <see cref="MaxLinkLength"/>:
   /// a mail program might then open with half a message, so the text is better shown
   /// for copying.
   /// </summary>
   public bool FitsInALink => MailtoUri.OriginalString.Length <= MaxLinkLength;



   /// <summary>Writes the invitation.</summary>
   /// <param name="repositoryUrl">
   /// The project's public page, which the release and documentation links hang off:
   /// an http(s) address made of a host and a path, nothing else.
   /// </param>
   /// <param name="version">The running app's version, named at the end so the recipient knows what the sender uses.</param>
   /// <exception cref="ArgumentException">
   /// <paramref name="repositoryUrl"/> is not an absolute http(s) address, or carries
   /// a user name, a query, or a fragment; or <paramref name="version"/> holds a
   /// control character. Either would put something into the message that the links
   /// and lines of it are not meant to hold.
   /// </exception>
   public static ShareInvitation Create(string repositoryUrl, string version)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);
      ArgumentException.ThrowIfNullOrWhiteSpace(version);
      if(!Uri.TryCreate(repositoryUrl.Trim(), UriKind.Absolute, out Uri? page) || page.Scheme is not ("https" or "http"))
      {
         throw new ArgumentException($"'{repositoryUrl}' is not a web address.", nameof(repositoryUrl));
      }
      if(page.UserInfo.Length > 0 || page.Query.Length > 0 || page.Fragment.Length > 0)
      {
         throw new ArgumentException(
             "The project's page is a host and a path: no user name, no query, no fragment.", nameof(repositoryUrl));
      }
      if(version.Any(char.IsControl))
      {
         throw new ArgumentException("The version is one line of text.", nameof(version));
      }

      // From the parsed address, not from the text as given: what goes into the
      // message is the scheme, the host, and the path, and nothing more.
      string repository = page.GetLeftPart(UriPartial.Path).TrimEnd('/');
      string[] lines =
      [
          "Hi,",
          "",
          "I use gclo and thought you might like it. It is a small Windows app (with a command-line version) that "
          + "clones every repository of an organization or account on GitHub or on a Gitea server into a folder and keeps them up to date.",
          "",
          "Download the latest release:",
          repository + "/releases/latest",
          "Get gclo-stable-Setup.exe there. It installs for your user only (no admin rights) and updates itself.",
          "",
          "Command line only, with Scoop:",
          "scoop bucket add gclo " + repository,
          "scoop install gclo",
          "",
          "How to use it: " + repository + "#readme",
          "",
          "If you try it, tell me what you think.",
          "",
          "(sent from gclo " + version.Trim() + ")",
      ];
      return new ShareInvitation(
          "Try gclo: clone and update every repository of a GitHub or Gitea organization", string.Join("\r\n", lines));
   }
}
