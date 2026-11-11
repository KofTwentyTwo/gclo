/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;
using System.Net;
using System.Text;


namespace gclo.Engine.Tests;


/// <summary>
/// A Gitea server for the offline suite: answers the requests of <see cref="GiteaApi"/>
/// from a table of paths, and records what it was asked and with which token.
/// Anything not in the table is answered the way Gitea answers an unknown path: 404
/// with a JSON reason.
/// </summary>
internal sealed class FakeGiteaServer : HttpMessageHandler
{
   /// <summary>The address accounts of this server are given.</summary>
   public static readonly Uri Address = new("https://git.example.test/");

   private readonly ConcurrentQueue<(Uri Address, string? Authorization)> _requests = new();

   private readonly ConcurrentDictionary<string, Func<HttpResponseMessage>> _answers = new(StringComparer.Ordinal);



   /// <summary>Called for every request before it is answered; throwing here is a request that fails on the way.</summary>
   public Func<HttpRequestMessage, CancellationToken, Task>? OnRequest { get; set; }

   /// <summary>What a path that is not in the table is answered with; Gitea's own 404 when null.</summary>
   public Func<HttpResponseMessage>? Otherwise { get; set; }

   /// <summary>Every request so far: its address and its Authorization header.</summary>
   public IReadOnlyList<(Uri Address, string? Authorization)> Requests => [.. _requests];

   /// <summary>The paths (with query, after <c>/api/v1/</c>) asked for so far.</summary>
   public IReadOnlyList<string> Asked => [.. _requests.Select(r => r.Address.PathAndQuery["/api/v1/".Length..])];



   /// <summary>A client of this server at <see cref="Address"/>.</summary>
   public GiteaApi Api(TimeSpan? requestTimeout = null) => new(Address, new HttpClient(this), requestTimeout);



   /// <summary>Answers <paramref name="path"/> (after <c>/api/v1/</c>, with its query) with what <paramref name="answer"/> makes.</summary>
   public FakeGiteaServer On(string path, Func<HttpResponseMessage> answer)
   {
      _answers[path] = answer;
      return this;
   }



   /// <summary>Answers page <paramref name="page"/> of the list at <paramref name="path"/> with <paramref name="items"/>.</summary>
   /// <param name="path">The list's path, without query.</param>
   /// <param name="page">The page, from 1.</param>
   /// <param name="items">The JSON of each item.</param>
   /// <param name="more">What the Link header says about a next page; null for no Link header.</param>
   public FakeGiteaServer OnPage(string path, int page, IEnumerable<string> items, bool? more = null)
       => On($"{path}?page={page}&limit={GiteaApi.PageSize}", () => Json($"[{string.Join(',', items)}]", link: more));



   /// <summary>A JSON answer.</summary>
   public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, bool? link = null)
   {
      var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
      if(link is bool more)
      {
         response.Headers.TryAddWithoutValidation(
             "Link",
             more
                 ? "<https://elsewhere.example.test/api/v1/x?page=2>; rel=\"next\",<https://elsewhere.example.test/api/v1/x?page=9>; rel=\"last\""
                 : "<https://elsewhere.example.test/api/v1/x?page=1>; rel=\"first\"");
      }
      return response;
   }



   /// <summary>The answer of a refused request, as Gitea gives it.</summary>
   public static HttpResponseMessage Refusal(HttpStatusCode status, string message)
       => Json($$"""{"message":"{{message}}","url":"https://git.example.test/api/swagger"}""", status);



   /// <summary>A repository as the API describes it.</summary>
   public static string Repo(
       string name, string? owner = "acme", string? branch = "main", bool archived = false, bool empty = false)
   {
      string ownerJson = owner is null ? "null" : $$"""{"login":"{{owner}}","id":7}""";
      string branchJson = branch is null ? "null" : $"\"{branch}\"";
      return $$"""
         {"id":1,"name":"{{name}}","full_name":"{{owner}}/{{name}}","owner":{{ownerJson}},
          "clone_url":"https://attacker.example.test/{{owner}}/{{name}}.git","default_branch":{{branchJson}},
          "archived":{{(archived ? "true" : "false")}},"empty":{{(empty ? "true" : "false")}},"mirror":false}
         """;
   }



   /// <inheritdoc/>
   protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
   {
      _requests.Enqueue((request.RequestUri!, request.Headers.Authorization?.ToString()));
      if(OnRequest is { } onRequest)
      {
         await onRequest(request, cancellationToken);
      }
      cancellationToken.ThrowIfCancellationRequested();

      string path = request.RequestUri!.PathAndQuery;
      string key = path.StartsWith("/api/v1/", StringComparison.Ordinal) ? path["/api/v1/".Length..] : path;
      return _answers.TryGetValue(key, out Func<HttpResponseMessage>? answer)
          ? answer()
          : Otherwise?.Invoke() ?? Refusal(HttpStatusCode.NotFound, "Not Found");
   }
}
