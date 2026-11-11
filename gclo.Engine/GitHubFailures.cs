/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Text.Json;
using Octokit;


namespace gclo.Engine;


/// <summary>
/// The one place that says what a failed GitHub request means to a caller: which
/// failures are the token's (fix it), which are access (grant it), and which are
/// not the caller's at all (the network, a timeout, GitHub itself: retry later).
/// Both listers go through it, so they cannot drift apart, and a failure it does
/// not know stays what it is: an unexpected error.
/// </summary>
internal static class GitHubFailures
{
   /// <summary>
   /// Waits for one gateway request, or for the caller's cancellation, whichever
   /// comes first. The gateway cannot cancel a request, so a canceled one is left
   /// to end on its own (at the HTTP timeout at the latest); whatever it ends with
   /// is looked at here, so it never surfaces as a failure nobody observed.
   /// </summary>
   public static Task<T> WaitFor<T>(Task<T> request, CancellationToken cancellationToken)
   {
      _ = request.ContinueWith(
          static abandoned => _ = abandoned.Exception,
          CancellationToken.None,
          TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
          TaskScheduler.Default);
      return request.WaitAsync(cancellationToken);
   }



   /// <summary>
   /// True when <paramref name="exception"/> is a failure with a known meaning, which
   /// <see cref="Translate"/> then puts into words.
   /// </summary>
   /// <param name="exception">What a gateway call threw.</param>
   /// <param name="cancellationToken">
   /// The caller's token: a canceled request is a timeout only when the caller did
   /// not cancel it. This is the one moment the token is looked at.
   /// </param>
   public static bool Knows(Exception exception, CancellationToken cancellationToken)
       => Find(exception, canceledByCaller: cancellationToken.IsCancellationRequested) is not null;



   /// <summary>
   /// The <see cref="GitHubAccessException"/> that says what <paramref name="exception"/>
   /// means to a caller. Only for a failure that <see cref="Knows"/> accepted: the
   /// decision whether a canceled request was the caller's doing was made there and
   /// is not made again here, so a cancellation that arrives in between changes nothing.
   /// </summary>
   /// <exception cref="ArgumentException">The failure has no known meaning.</exception>
   public static GitHubAccessException Translate(Exception exception)
       => Find(exception, canceledByCaller: false)
          ?? throw new ArgumentException("This failure has no known meaning; ask Knows first.", nameof(exception));



   private static GitHubAccessException? Find(Exception exception, bool canceledByCaller)
   {
      ///////////////////////////////////////////////////////////////////////////////
      // Order matters: the throttling exceptions derive from ForbiddenException, //
      // and every one of these but the last two derives from ApiException.       //
      ///////////////////////////////////////////////////////////////////////////////
      return exception switch
      {
         AuthorizationException => new GitHubAccessException(
             GitHubAccessKind.Unauthorized,
             "GitHub rejected the token (401). Check that the PAT is complete, has not expired or been revoked, "
             + "and has 'repo' (classic) or repository read access (fine-grained).",
             exception),

         RateLimitExceededException limit => new GitHubAccessException(
             GitHubAccessKind.RateLimited, $"GitHub API rate limit exceeded; it resets at {limit.Reset:u}.", exception),

         // A burst of requests, not a scope problem; Octokit models it beside (not
         // under) AbuseException, so both get the same translation.
         SecondaryRateLimitExceededException => new GitHubAccessException(
             GitHubAccessKind.RateLimited,
             "GitHub's secondary rate limit was hit (too many requests in a short time); retry in a minute.",
             exception),

         AbuseException abuse => new GitHubAccessException(
             GitHubAccessKind.RateLimited,
             "GitHub's secondary rate limit was hit (too many requests in a short time); "
             + $"retry in {abuse.RetryAfterSeconds ?? 60} seconds.",
             exception),

         LoginAttemptsExceededException => new GitHubAccessException(
             GitHubAccessKind.RateLimited,
             "GitHub temporarily blocked this token after too many failed attempts; wait a few minutes and retry.",
             exception),

         // GitHub answers a rate limit with 403 or with 429; Octokit has no type for the second.
         ApiException { StatusCode: HttpStatusCode.TooManyRequests } => new GitHubAccessException(
             GitHubAccessKind.RateLimited,
             "GitHub is limiting requests from this token (429: too many requests); retry in a minute.",
             exception),

         ForbiddenException or LegalRestrictionException => new GitHubAccessException(
             GitHubAccessKind.Forbidden, Refused((ApiException)exception), exception),

         ApiException { StatusCode: HttpStatusCode.ProxyAuthenticationRequired } => new GitHubAccessException(
             GitHubAccessKind.Unavailable,
             "A proxy on the way to GitHub asked gclo to sign in (HTTP 407), which it cannot do. "
             + "Check the proxy settings of this machine.",
             exception),

         ApiException { StatusCode: >= HttpStatusCode.InternalServerError } server => new GitHubAccessException(
             GitHubAccessKind.Unavailable,
             $"GitHub answered with an error on its side (HTTP {(int)server.StatusCode}); retry later.",
             exception),

         HttpRequestException network => new GitHubAccessException(
             GitHubAccessKind.Unavailable, ForgeFailures.Unreachable("GitHub", network.HttpRequestError), exception),

         OperationCanceledException when !canceledByCaller => new GitHubAccessException(
             GitHubAccessKind.Unavailable, "GitHub did not answer in time; retry later.", exception),

         _ => null,
      };
   }



   /// <summary>
   /// The words for a refused request. GitHub's own reason is part of them only when
   /// it came as the reason of an API answer; a page from something in between (a web
   /// filter, a proxy) is not a reason and is left out.
   /// </summary>
   private static string Refused(ApiException refusal)
   {
      const string Advice = "If the organization uses SAML single sign-on, authorize the token for it; "
          + "otherwise check what the token is allowed to access.";
      string reason = ReasonOf(refusal);
      return reason.Length == 0
          ? $"GitHub refused access ({(int)refusal.StatusCode}). {Advice}"
          : $"GitHub refused access ({(int)refusal.StatusCode}): {reason} {Advice}";
   }



   /// <summary>
   /// The reason GitHub's API gave for a refusal: the "message" of the JSON document it
   /// answered with. Anything else (no body, a page of HTML) has no reason in it;
   /// Octokit would hand over such a body whole.
   /// </summary>
   private static string ReasonOf(ApiException refusal)
   {
      if(refusal.HttpResponse?.Body is not string body)
      {
         return "";
      }

      try
      {
         using var document = JsonDocument.Parse(body);
         return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("message", out JsonElement message)
                && message.ValueKind == JsonValueKind.String
             ? ForgeFailures.OneLine(message.GetString())
             : "";
      }
      catch(JsonException)
      {
         return "";
      }
   }
}
