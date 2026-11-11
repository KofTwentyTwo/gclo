/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

namespace gclo.Engine.Tests;


/// <summary>
/// What the listers of every forge promise their callers, stated once and run against
/// each forge (see the classes that derive from this one). The sync engine, the view
/// models and the command line are written against these promises, not against a
/// forge.
/// </summary>
public abstract class ListerContractTests
{
   /// <summary>The token every call is made with.</summary>
   protected const string Token = "contract-test-token";



   /// <summary>The repository lister under test.</summary>
   protected abstract IRepositoryLister Repositories { get; }

   /// <summary>The organization lister under test.</summary>
   protected abstract IOrganizationLister Organizations { get; }



   /// <summary>The forge has an organization of that name whose list holds these repositories, in this order.</summary>
   protected abstract void GivenAnOrganization(string name, params string[] repositories);



   /// <summary>The token belongs to that account, which is a member of these organizations (as the forge lists them).</summary>
   protected abstract void GivenTheTokenBelongsTo(string account, params string[] organizations);



   /// <summary>The forge knows neither an organization nor an account of that name.</summary>
   protected abstract void GivenNothingIsCalled(string name);



   /// <summary>The forge rejects the token.</summary>
   protected abstract void GivenTheTokenIsRejected();



   /// <summary>The forge fails on its side.</summary>
   protected abstract void GivenTheForgeFails();



   /// <summary>How many requests the forge has received.</summary>
   protected abstract int RequestsMade { get; }



   [Fact]
   public async Task Repositories_AreSortedByName_IgnoringCase_AndEachIsListedOnce()
   {
      GivenAnOrganization("acme", "zeta", "Alpha", "mid", "ALPHA");

      IReadOnlyList<RepoDescriptor> repos = await Repositories.ListOrganizationRepositoriesAsync("acme", Token);

      Assert.Equal(["Alpha", "mid", "zeta"], repos.Select(r => r.Name), StringComparer.Ordinal);
   }



   [Fact]
   public async Task Repositories_HaveAnHttpsCloneAddress_ThatEndsInTheirName()
   {
      GivenAnOrganization("acme", "tool");

      RepoDescriptor repo = Assert.Single(await Repositories.ListOrganizationRepositoriesAsync("acme", Token));

      var address = new Uri(repo.CloneUrl);
      Assert.Equal(Uri.UriSchemeHttps, address.Scheme);
      Assert.EndsWith("/tool.git", address.AbsolutePath, StringComparison.Ordinal);
   }



   [Fact]
   public async Task AnOrganizationWithoutRepositories_IsAnEmptyList_NotAFailure()
   {
      GivenAnOrganization("empty");

      Assert.Empty(await Repositories.ListOrganizationRepositoriesAsync("empty", Token));
   }



   [Fact]
   public async Task ANameNothingIsCalled_IsAForgeFailure_OfTheKindNotFound()
   {
      GivenTheTokenBelongsTo("me");
      GivenNothingIsCalled("nobody");

      ForgeAccessException ex = await Assert.ThrowsAnyAsync<ForgeAccessException>(
          () => Repositories.ListOrganizationRepositoriesAsync("nobody", Token));

      Assert.Equal(ForgeAccessKind.NotFound, ex.Kind);
   }



   [Fact]
   public async Task ARejectedToken_IsAForgeFailure_OfTheKindUnauthorized_FromBothListers()
   {
      GivenTheTokenIsRejected();

      ForgeAccessException fromRepositories = await Assert.ThrowsAnyAsync<ForgeAccessException>(
          () => Repositories.ListOrganizationRepositoriesAsync("acme", Token));
      ForgeAccessException fromOrganizations = await Assert.ThrowsAnyAsync<ForgeAccessException>(
          () => Organizations.ListOrganizationsAsync(Token));

      Assert.Equal(ForgeAccessKind.Unauthorized, fromRepositories.Kind);
      Assert.Equal(ForgeAccessKind.Unauthorized, fromOrganizations.Kind);
      Assert.DoesNotContain(Token, fromRepositories.Message, StringComparison.Ordinal);
      Assert.DoesNotContain(Token, fromOrganizations.Message, StringComparison.Ordinal);
   }



   [Fact]
   public async Task AForgeThatFails_IsAForgeFailure_OfTheKindUnavailable_FromBothListers()
   {
      GivenTheForgeFails();

      ForgeAccessException fromRepositories = await Assert.ThrowsAnyAsync<ForgeAccessException>(
          () => Repositories.ListOrganizationRepositoriesAsync("acme", Token));
      ForgeAccessException fromOrganizations = await Assert.ThrowsAnyAsync<ForgeAccessException>(
          () => Organizations.ListOrganizationsAsync(Token));

      Assert.Equal(ForgeAccessKind.Unavailable, fromRepositories.Kind);
      Assert.Equal(ForgeAccessKind.Unavailable, fromOrganizations.Kind);
   }



   [Fact]
   public async Task Organizations_BeginWithTheTokensOwnAccount_ThenTheRestSorted_EachOnce()
   {
      GivenTheTokenBelongsTo("me", "zeta", "Alpha", "ME");

      IReadOnlyList<string> names = await Organizations.ListOrganizationsAsync(Token);

      Assert.Equal(["me", "Alpha", "zeta"], names, StringComparer.Ordinal);
   }



   [Fact]
   public async Task ACallThatIsAlreadyCanceled_AsksTheForgeNothing()
   {
      using var cts = new CancellationTokenSource();
      cts.Cancel();

      await Assert.ThrowsAnyAsync<OperationCanceledException>(
          () => Repositories.ListOrganizationRepositoriesAsync("acme", Token, cts.Token));
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Organizations.ListOrganizationsAsync(Token, cts.Token));

      Assert.Equal(0, RequestsMade);
   }



   [Theory]
   [InlineData("", Token)]
   [InlineData("acme", " ")]
   public async Task BlankArguments_AreRefused_AsArguments(string organization, string token)
   {
      await Assert.ThrowsAnyAsync<ArgumentException>(() => Repositories.ListOrganizationRepositoriesAsync(organization, token));
      await Assert.ThrowsAnyAsync<ArgumentException>(() => Organizations.ListOrganizationsAsync(" "));

      Assert.Equal(0, RequestsMade);
   }



   [Fact]
   public async Task ANameThatIsNotOnePlainName_IsNotFound_AndIsNotSentToTheForge()
   {
      ForgeAccessException ex = await Assert.ThrowsAnyAsync<ForgeAccessException>(
          () => Repositories.ListOrganizationRepositoriesAsync("../elsewhere", Token));

      Assert.Equal(ForgeAccessKind.NotFound, ex.Kind);
      Assert.Equal(0, RequestsMade);
   }
}
