/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using gclo.Engine;


namespace gclo.ViewModels;


/// <summary>
/// What the desktop app says about a forge where it used to say "GitHub": the
/// page a token is made on, how the token is called there, and what it has to be
/// allowed to do. One place, so the wizard and an account's page say the same.
/// </summary>
internal static class ForgeTexts
{
   private static readonly Uri s_gitHubTokenPage = new("https://github.com/settings/tokens");



   /// <summary>
   /// The page a person makes a token on: GitHub's, or the one of the Gitea server
   /// itself. Null when the place is not known (an account whose stored address
   /// cannot be used): there is no page to point at then.
   /// </summary>
   public static Uri? TokenPage(ForgeEndpoint? place)
       => place is null ? null
           : place.Server is null ? s_gitHubTokenPage
           : new Uri(place.Server, "user/settings/applications");



   /// <summary>What the forge calls the token.</summary>
   public static string TokenName(ForgeKind forge)
       => forge == ForgeKind.GitHub ? "Personal access token" : "Access token";



   /// <summary>What the token has to be allowed to do, as the end of a sentence.</summary>
   public static string TokenNeeds(ForgeKind forge)
       => forge == ForgeKind.GitHub
           ? "needs repository read access"
           : "needs read access to repositories, organizations and the user";
}
