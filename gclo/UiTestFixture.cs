/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using gclo.Engine;


namespace gclo;


/// <summary>
/// Offline seam for the FlaUI suite (#54, #33): when the process runs with
/// <c>GCLO_UITEST_FIXTURE=1</c> <b>and</b> a workspace's token is exactly
/// <see cref="Token"/>, the forge and git are answered by this fixture instead of the
/// network - a fixed organization with three repositories (another one for a Gitea
/// server, with clone addresses on that server), and a git client that
/// succeeds without touching disk. Any other token (even with the variable set) goes
/// to the real services, so the existing offline-input tests keep their meaning
/// (a dummy token still gets GitHub's 401). Production never sets the variable, and an
/// installed build ignores it: the seam exists for a build run from its output folder
/// by the test suite, not for the app people install (#145).
/// </summary>
internal static class UiTestFixture
{
   /// <summary>The magic token that routes a workspace to the fixture.</summary>
   public const string Token = "gclo-uitest-fixture-token";

   /// <summary>The organization the fixture lists.</summary>
   public const string Organization = "fixture-org";

   /// <summary>The organization the fixture lists for a Gitea server (#206).</summary>
   public const string GiteaOrganization = "fixture-gitea-org";

   /// <summary>
   /// True when the process was started for the UI suite with the fixture enabled, and
   /// is not an installed build.
   /// </summary>
   public static bool IsEnabled { get; } =
       string.Equals(Environment.GetEnvironmentVariable("GCLO_UITEST_FIXTURE"), "1", StringComparison.Ordinal)
       && !IsInstalledBuild();

   private static readonly IReadOnlyList<RepoDescriptor> s_repositories =
   [
       new("alpha", "https://github.com/fixture-org/alpha.git", "main", IsArchived: false),
         new("bravo", "https://github.com/fixture-org/bravo.git", "develop", IsArchived: true),
         new("charlie", "https://github.com/fixture-org/charlie.git", "main", IsArchived: false),
     ];



   /// <summary>
   /// True when the app was installed by Velopack (it then has an app id). A build run
   /// from its output folder, which is what the UI suite launches, has none.
   /// </summary>
   private static bool IsInstalledBuild()
   {
      try
      {
         return !string.IsNullOrEmpty(Velopack.Locators.VelopackLocator.Current?.AppId);
      }
      catch(Exception)
      {
         // No locator for this platform or layout: not an installed build.
         return false;
      }
   }



   /// <summary>
   /// The lister the shell uses for <paramref name="place"/> (null: GitHub): the real
   /// one, answered by the fixture for the fixture token when the fixture is enabled.
   /// </summary>
   public static IRepositoryLister WrapRepositoryLister(IRepositoryLister real, ForgeEndpoint? place = null)
       => IsEnabled ? new RepositoryLister(real, RepositoriesAt(place)) : real;



   /// <summary>The organization lister for <paramref name="place"/>, wrapped the same way.</summary>
   public static IOrganizationLister WrapOrganizationLister(IOrganizationLister real, ForgeEndpoint? place = null)
       => IsEnabled ? new OrganizationLister(real, place?.Server is null ? Organization : GiteaOrganization) : real;



   /// <summary>The fixture's repositories as the place would list them: on a Gitea server, with clone addresses on that server.</summary>
   private static IReadOnlyList<RepoDescriptor> RepositoriesAt(ForgeEndpoint? place)
   {
      if(place?.Server is not Uri server)
      {
         return s_repositories;
      }

      List<RepoDescriptor> onTheServer = [];
      foreach(RepoDescriptor repository in s_repositories)
      {
         onTheServer.Add(repository with { CloneUrl = new Uri(server, $"{GiteaOrganization}/{repository.Name}.git").AbsoluteUri });
      }
      return onTheServer;
   }



   public static IGitClient WrapGitClient(IGitClient real)
       => IsEnabled ? new GitClient(real) : real;



   private sealed class RepositoryLister(IRepositoryLister real, IReadOnlyList<RepoDescriptor> repositories) : IRepositoryLister
   {
      public Task<IReadOnlyList<RepoDescriptor>> ListOrganizationRepositoriesAsync(
          string organization, string token, CancellationToken cancellationToken = default)
          => string.Equals(token, Token, StringComparison.Ordinal)
              ? Task.FromResult(repositories)
              : real.ListOrganizationRepositoriesAsync(organization, token, cancellationToken);
   }



   private sealed class OrganizationLister(IOrganizationLister real, string organization) : IOrganizationLister
   {
      public Task<IReadOnlyList<string>> ListOrganizationsAsync(string token, CancellationToken cancellationToken = default)
          => string.Equals(token, Token, StringComparison.Ordinal)
              ? Task.FromResult<IReadOnlyList<string>>([organization])
              : real.ListOrganizationsAsync(token, cancellationToken);
   }



   /// <summary>Succeeds without writing anything; the real client handles every other token.</summary>
   private sealed class GitClient(IGitClient real) : IGitClient
   {
      // No token is passed here, so the real client answers for everyone. The
      // fixture's own clones write nothing, so its folders are never repositories
      // and the fixture token always takes the clone path (#145).
      public bool IsValidRepository(string path) => real.IsValidRepository(path);



      public Task CloneAsync(string url, string path, string token, Action<double>? onProgress, CancellationToken cancellationToken)
          => string.Equals(token, Token, StringComparison.Ordinal) ? Task.CompletedTask : real.CloneAsync(url, path, token, onProgress, cancellationToken);



      public Task FetchAndPullAsync(string url, string path, string token, CancellationToken cancellationToken)
          => string.Equals(token, Token, StringComparison.Ordinal) ? Task.CompletedTask : real.FetchAndPullAsync(url, path, token, cancellationToken);



      public Task ApplyRecoveryAsync(string path, PathRecovery recovery, CancellationToken cancellationToken)
          => real.ApplyRecoveryAsync(path, recovery, cancellationToken);
   }
}
