namespace gclo.Engine;

/// <summary>Lists every repository in a GitHub organization.</summary>
public interface IRepositoryLister
{
    /// <summary>Returns all repositories in the organization, following pagination to the end.</summary>
    Task<IReadOnlyList<RepoDescriptor>> ListOrganizationRepositoriesAsync(
        string organization, string token, CancellationToken cancellationToken = default);
}
