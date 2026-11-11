/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Net;


namespace gclo.Engine;


/// <summary>
/// A Gitea server answered a request with a status that is not success. Internal to
/// the Gitea listers: they decide what a status means where it occurred (a 404 for
/// an organization is "perhaps a user account"), and <see cref="GiteaApi.Translate"/>
/// puts the rest into words.
/// </summary>
internal sealed class GiteaStatusException : Exception
{
   /// <summary>The HTTP status of the answer.</summary>
   public HttpStatusCode Status { get; }

   /// <summary>The server's own reason, on one line and of bounded length; empty when it gave none.</summary>
   public string Reason { get; }



   /// <summary>Creates the exception for a status and the reason the server gave.</summary>
   public GiteaStatusException(HttpStatusCode status, string reason)
       : base($"The server answered with HTTP {(int)status}.")
   {
      Status = status;
      Reason = reason;
   }
}
