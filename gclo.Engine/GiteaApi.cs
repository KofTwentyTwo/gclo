/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;


namespace gclo.Engine;


/// <summary>
/// The requests gclo makes to one Gitea server, and what their failures mean.
/// </summary>
/// <remarks>
/// <para>
/// The server's address is one a user typed, so the server is not trusted beyond
/// being the place the user chose to send their token to:
/// </para>
/// <list type="bullet">
/// <item>Every request goes to the address the account names (HTTPS, checked by
/// <see cref="GiteaServerUrl"/>), with the token in its <c>Authorization</c> header.
/// Redirects are not followed, so the token is not carried anywhere else.</item>
/// <item>Certificates are validated by the platform. There is no switch for that.</item>
/// <item>Clone addresses are built from the account's address
/// (<see cref="CloneUrl"/>), not read from the server's answers: the git client
/// gives the token to the host of the clone address.</item>
/// <item>Answers are bounded in size, lists in length, and a request in time.</item>
/// <item>The server's own words reach a message only as the reason of a refusal,
/// on one line and cut to a length.</item>
/// </list>
/// </remarks>
internal sealed class GiteaApi
{
   /// <summary>Items asked for per page. The server may give fewer: it has a limit of its own.</summary>
   public const int PageSize = 50;

   /// <summary>The most pages one list is read for: a server whose list does not end is not followed for ever.</summary>
   public const int MaxPages = 2000;

   /// <summary>The largest answer that is read.</summary>
   private const int MaxAnswerBytes = 32 * 1024 * 1024;

   /// <summary>The most that is read of a refusal to find the server's reason in it.</summary>
   private const int MaxRefusalBytes = 64 * 1024;

   /// <summary>
   /// How the requests of production are carried: redirects are not followed (the
   /// token stays on the host the account names), and certificates are validated by
   /// the platform's own rules, with no callback that could accept another.
   /// </summary>
   internal static readonly SocketsHttpHandler SharedHandler = new()
   {
      AllowAutoRedirect = false,
      PooledConnectionLifetime = TimeSpan.FromMinutes(5),
   };

   /// <summary>
   /// One client for every server: connections are pooled per host, and nothing that
   /// belongs to one account (its token) is kept on it.
   /// </summary>
   private static readonly HttpClient s_shared = new(SharedHandler)
   {
      Timeout = Timeout.InfiniteTimeSpan, // each request has its own limit, which also covers reading the answer
   };

   private readonly Uri _api;

   private readonly HttpClient _http;

   private readonly TimeSpan _requestTimeout;



   /// <summary>The server's base address, as <see cref="GiteaServerUrl"/> normalizes it.</summary>
   public Uri Server { get; }

   /// <summary>The server as messages name it: its host, and its port when that is not 443.</summary>
   public string Host { get; }



   /// <summary>Creates the client for the server at <paramref name="server"/>.</summary>
   /// <param name="server">The server's address; held to the rules of <see cref="GiteaServerUrl"/>.</param>
   /// <param name="http">Test seam: what carries the requests. The shared client when null.</param>
   /// <param name="requestTimeout">Test seam: how long one request may take, answer included. 100 seconds when null.</param>
   /// <exception cref="ArgumentException"><paramref name="server"/> is not an address a Gitea account can have.</exception>
   public GiteaApi(Uri server, HttpClient? http = null, TimeSpan? requestTimeout = null)
   {
      ArgumentNullException.ThrowIfNull(server);
      if(!GiteaServerUrl.TryNormalize(server.OriginalString, out Uri? normalized, out string? reason))
      {
         throw new ArgumentException($"'{server.OriginalString}' cannot be the address of a Gitea server: {reason}.", nameof(server));
      }

      Server = normalized;
      Host = normalized.Authority;
      _api = new Uri(normalized, "api/v1/");
      _http = http ?? s_shared;
      _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(100);
   }



   /// <summary>
   /// The address a repository is cloned from: the account's server address, the
   /// owner, the name. Built here, not taken from the server's answer, so the host
   /// that gets the token for the clone is the one the account names.
   /// </summary>
   public string CloneUrl(string owner, string name)
       => new Uri(Server, $"{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}.git").AbsoluteUri;



   /// <summary>The name of the account the token belongs to.</summary>
   /// <exception cref="GiteaUnexpectedAnswerException">The answer names no account.</exception>
   public async Task<string> GetOwnNameAsync(string token, CancellationToken cancellationToken)
   {
      (GiteaAccount account, _) = await SendAsync("user", token, GiteaJsonContext.Default.GiteaAccount, cancellationToken)
          .ConfigureAwait(false);
      return account.Name ?? throw new GiteaUnexpectedAnswerException("The answer to 'who am I' names no account.");
   }



   /// <summary>
   /// Every item of the list at <paramref name="path"/>, page by page. The server says
   /// whether there is a next page (its <c>Link</c> header); where it does not say,
   /// an empty page ends the list. A page that is not full ends nothing: the server
   /// has a page size of its own, and it leaves out what the token may not see.
   /// </summary>
   /// <exception cref="GiteaUnexpectedAnswerException">The list did not end within <see cref="MaxPages"/> pages.</exception>
   public async Task<List<T>> ListAsync<T>(string path, string token, JsonTypeInfo<List<T?>> type, CancellationToken cancellationToken)
       where T : class
   {
      var all = new List<T>();
      for(int page = 1; page <= MaxPages; page++)
      {
         cancellationToken.ThrowIfCancellationRequested();
         (List<T?> items, bool? more) = await SendAsync($"{path}?page={page}&limit={PageSize}", token, type, cancellationToken)
             .ConfigureAwait(false);
         all.AddRange(items.OfType<T>());
         if(more == false || (more is null && items.Count == 0))
         {
            return all;
         }
      }
      throw new GiteaUnexpectedAnswerException($"A list did not end after {MaxPages} pages.");
   }



   /// <summary>
   /// True when <paramref name="failure"/> is a failure with a known meaning, which
   /// <see cref="Translate"/> then puts into words. One without stays what it is: an
   /// unexpected error, or the caller's own cancellation.
   /// </summary>
   /// <param name="failure">What a request threw.</param>
   /// <param name="cancellationToken">
   /// The caller's token: a canceled request is a timeout only when the caller did
   /// not cancel it. This is the one moment the token is looked at.
   /// </param>
   public bool Knows(Exception failure, CancellationToken cancellationToken)
       => Explain(failure, canceledByCaller: cancellationToken.IsCancellationRequested) is not null;



   /// <summary>
   /// The <see cref="ForgeAccessException"/> that says what <paramref name="failure"/>
   /// means to a caller. Only for a failure that <see cref="Knows"/> accepted: whether
   /// a canceled request was the caller's doing was decided there and is not decided
   /// again here, so a cancellation that arrives in between changes nothing.
   /// </summary>
   /// <exception cref="ArgumentException">The failure has no known meaning.</exception>
   public ForgeAccessException Translate(Exception failure)
       => Explain(failure, canceledByCaller: false)
          ?? throw new ArgumentException("This failure has no known meaning; ask Knows first.", nameof(failure));



   private ForgeAccessException? Explain(Exception failure, bool canceledByCaller)
   {
      const string CheckTheAddress = "Check the server address of this account.";
      return failure switch
      {
         GiteaStatusException { Status: HttpStatusCode.Unauthorized } => new ForgeAccessException(
             ForgeAccessKind.Unauthorized,
             $"{Host} rejected the token (401). Check that the access token is complete and has not been deleted or expired.",
             failure),

         GiteaStatusException { Status: HttpStatusCode.Forbidden } refused => new ForgeAccessException(
             ForgeAccessKind.Forbidden,
             (refused.Reason.Length == 0 ? $"{Host} refused access (403)." : $"{Host} refused access (403): {refused.Reason}")
             + " Check what the access token is allowed to do: gclo reads repositories, organizations and the user "
             + "(the scopes read:repository, read:organization and read:user).",
             failure),

         GiteaStatusException { Status: HttpStatusCode.TooManyRequests } => new ForgeAccessException(
             ForgeAccessKind.RateLimited,
             $"{Host} is limiting requests from this token (429: too many requests); retry in a minute.",
             failure),

         GiteaStatusException { Status: HttpStatusCode.ProxyAuthenticationRequired } => new ForgeAccessException(
             ForgeAccessKind.Unavailable,
             $"A proxy on the way to {Host} asked gclo to sign in (HTTP 407), which it cannot do. "
             + "Check the proxy settings of this machine.",
             failure),

         GiteaStatusException { Status: >= HttpStatusCode.InternalServerError } server => new ForgeAccessException(
             ForgeAccessKind.Unavailable,
             $"{Host} answered with an error on its side (HTTP {(int)server.Status}); retry later.",
             failure),

         ///////////////////////////////////////////////////////////////////////////
         // What is left of the statuses says that the address is not a Gitea     //
         // server's: a redirect (never followed: the token stays where the       //
         // account says), a page that is not there, a request it does not take.  //
         ///////////////////////////////////////////////////////////////////////////
         GiteaStatusException { Status: >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest } redirect => new ForgeAccessException(
             ForgeAccessKind.NotFound,
             $"{Host} answered with a redirect (HTTP {(int)redirect.Status}) instead of the Gitea API. {CheckTheAddress} "
             + "It has to be the address the server itself uses.",
             failure),

         GiteaStatusException other => new ForgeAccessException(
             ForgeAccessKind.NotFound,
             $"There is no Gitea API at this address of {Host} (HTTP {(int)other.Status}). {CheckTheAddress}",
             failure),

         GiteaUnexpectedAnswerException => new ForgeAccessException(
             ForgeAccessKind.NotFound,
             $"{Host} did not answer like a Gitea server. {CheckTheAddress} "
             + "A sign-in page or a proxy in front of the server answers like this too.",
             failure),

         HttpRequestException network => new ForgeAccessException(
             ForgeAccessKind.Unavailable, ForgeFailures.Unreachable(Host, network.HttpRequestError), failure),

         OperationCanceledException when !canceledByCaller => new ForgeAccessException(
             ForgeAccessKind.Unavailable, $"{Host} did not answer in time; retry later.", failure),

         _ => null,
      };
   }



   /// <summary>
   /// One request: the answer read as <typeparamref name="T"/>, and whether the server
   /// says there is a next page (null when it does not say).
   /// </summary>
   private async Task<(T Value, bool? More)> SendAsync<T>(
       string relative, string token, JsonTypeInfo<T> type, CancellationToken cancellationToken)
       where T : class
   {
      using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      limit.CancelAfter(_requestTimeout);

      using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_api, relative));
      request.Headers.Authorization = new AuthenticationHeaderValue("token", token);
      request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
      request.Headers.UserAgent.Add(new ProductInfoHeaderValue("gclo", null));

      using HttpResponseMessage response = await _http
          .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
      if(!response.IsSuccessStatusCode)
      {
         throw new GiteaStatusException(response.StatusCode, await ReasonOfAsync(response, limit.Token).ConfigureAwait(false));
      }

      byte[] answer = await ReadAsync(response.Content, MaxAnswerBytes, limit.Token).ConfigureAwait(false)
          ?? throw new GiteaUnexpectedAnswerException("The answer is larger than gclo reads.");
      T? value;
      try
      {
         value = JsonSerializer.Deserialize(answer, type);
      }
      catch(JsonException notTheApi)
      {
         throw new GiteaUnexpectedAnswerException("The answer is not the JSON the API gives.", notTheApi);
      }

      bool? more = response.Headers.TryGetValues("Link", out IEnumerable<string>? links)
          ? links.Any(link => link.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase))
          : null;
      return (value ?? throw new GiteaUnexpectedAnswerException("The answer is empty."), more);
   }



   /// <summary>
   /// The reason the server gave for a refusal: the <c>message</c> of the JSON
   /// document it answered with. Anything else (no body, a page of HTML, too much)
   /// has no reason in it.
   /// </summary>
   private static async Task<string> ReasonOfAsync(HttpResponseMessage refusal, CancellationToken cancellationToken)
   {
      byte[]? body = await ReadAsync(refusal.Content, MaxRefusalBytes, cancellationToken).ConfigureAwait(false);
      if(body is null)
      {
         return "";
      }

      try
      {
         return ForgeFailures.OneLine(JsonSerializer.Deserialize(body, GiteaJsonContext.Default.GiteaRefusal)?.Message);
      }
      catch(JsonException)
      {
         return "";
      }
   }



   /// <summary>The bytes of <paramref name="content"/>, or null when there are more than <paramref name="most"/>.</summary>
   private static async Task<byte[]?> ReadAsync(HttpContent content, int most, CancellationToken cancellationToken)
   {
      using Stream body = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
      using var read = new MemoryStream();
      byte[] chunk = new byte[81920];
      while(true)
      {
         int count = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
         if(count == 0)
         {
            return read.ToArray();
         }
         if(read.Length + count > most)
         {
            return null;
         }
         read.Write(chunk, 0, count);
      }
   }
}
