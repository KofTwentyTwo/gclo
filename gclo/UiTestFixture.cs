using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using gclo.Engine;

namespace gclo
{
    /// <summary>
    /// Offline seam for the FlaUI suite (#54, #33): when the process runs with
    /// <c>GCLO_UITEST_FIXTURE=1</c> <b>and</b> a workspace's token is exactly
    /// <see cref="Token"/>, GitHub and git are answered by this fixture instead of the
    /// network - a fixed organization with three repositories, and a git client that
    /// succeeds without touching disk. Any other token (even with the variable set) goes
    /// to the real services, so the existing offline-input tests keep their meaning
    /// (a dummy token still gets GitHub's 401). Production never sets the variable.
    /// </summary>
    internal static class UiTestFixture
    {
        /// <summary>The magic token that routes a workspace to the fixture.</summary>
        public const string Token = "gclo-uitest-fixture-token";

        /// <summary>The organization the fixture lists.</summary>
        public const string Organization = "fixture-org";

        /// <summary>True when the process was started for the UI suite with the fixture enabled.</summary>
        public static bool IsEnabled { get; } =
            Environment.GetEnvironmentVariable("GCLO_UITEST_FIXTURE") == "1";

        private static readonly IReadOnlyList<RepoDescriptor> Repositories =
        [
            new("alpha", "https://github.com/fixture-org/alpha.git", "main", IsArchived: false),
            new("bravo", "https://github.com/fixture-org/bravo.git", "develop", IsArchived: true),
            new("charlie", "https://github.com/fixture-org/charlie.git", "main", IsArchived: false),
        ];

        public static IRepositoryLister WrapRepositoryLister(IRepositoryLister real)
            => IsEnabled ? new RepositoryLister(real) : real;

        public static IOrganizationLister WrapOrganizationLister(IOrganizationLister real)
            => IsEnabled ? new OrganizationLister(real) : real;

        public static IGitClient WrapGitClient(IGitClient real)
            => IsEnabled ? new GitClient(real) : real;

        private sealed class RepositoryLister(IRepositoryLister real) : IRepositoryLister
        {
            public Task<IReadOnlyList<RepoDescriptor>> ListOrganizationRepositoriesAsync(
                string organization, string token, CancellationToken cancellationToken = default)
                => token == Token
                    ? Task.FromResult(Repositories)
                    : real.ListOrganizationRepositoriesAsync(organization, token, cancellationToken);
        }

        private sealed class OrganizationLister(IOrganizationLister real) : IOrganizationLister
        {
            public Task<IReadOnlyList<string>> ListOrganizationsAsync(string token, CancellationToken cancellationToken = default)
                => token == Token
                    ? Task.FromResult<IReadOnlyList<string>>([Organization])
                    : real.ListOrganizationsAsync(token, cancellationToken);
        }

        /// <summary>Succeeds without writing anything; the real client handles every other token.</summary>
        private sealed class GitClient(IGitClient real) : IGitClient
        {
            public bool IsValidRepository(string path) => false;

            public Task CloneAsync(string url, string path, string token, Action<double>? onProgress, CancellationToken cancellationToken)
                => token == Token ? Task.CompletedTask : real.CloneAsync(url, path, token, onProgress, cancellationToken);

            public Task FetchAndPullAsync(string path, string token, CancellationToken cancellationToken)
                => token == Token ? Task.CompletedTask : real.FetchAndPullAsync(path, token, cancellationToken);

            public Task ApplyRecoveryAsync(string path, PathRecovery recovery, CancellationToken cancellationToken)
                => real.ApplyRecoveryAsync(path, recovery, cancellationToken);
        }
    }
}
