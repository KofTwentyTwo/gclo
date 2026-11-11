/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using static gclo.Engine.Tests.FakeGiteaServer;


namespace gclo.Engine.Tests;


/// <summary>The lister contract, kept by the Gitea listers.</summary>
public sealed class GiteaListerContractTests : ListerContractTests, IDisposable
{
   private readonly FakeGiteaServer _server = new();



   public void Dispose() => _server.Dispose();



   /// <inheritdoc/>
   protected override IRepositoryLister Repositories => new GiteaRepositoryLister(_server.Api());

   /// <inheritdoc/>
   protected override IOrganizationLister Organizations => new GiteaOrganizationLister(_server.Api());

   /// <inheritdoc/>
   protected override int RequestsMade => _server.Requests.Count;



   /// <inheritdoc/>
   protected override void GivenAnOrganization(string name, params string[] repositories)
       => _server.OnPage($"orgs/{name}/repos", 1, repositories.Select(repository => Repo(repository, owner: name)), more: false);



   /// <inheritdoc/>
   protected override void GivenTheTokenBelongsTo(string account, params string[] organizations)
       => _server
           .On("user", () => Json($$"""{"id":1,"login":"{{account}}"}"""))
           .OnPage("user/orgs", 1, organizations.Select(organization => $$"""{"id":2,"username":"{{organization}}"}"""), more: false);



   /// <inheritdoc/>
   protected override void GivenNothingIsCalled(string name)
   {
      // Nothing to set up: what the server does not know, it answers with 404.
   }



   /// <inheritdoc/>
   protected override void GivenTheTokenIsRejected()
       => _server.Otherwise = () => Refusal(HttpStatusCode.Unauthorized, "invalid username, password or token");



   /// <inheritdoc/>
   protected override void GivenTheForgeFails()
       => _server.Otherwise = () => Refusal(HttpStatusCode.ServiceUnavailable, "down for maintenance");
}
