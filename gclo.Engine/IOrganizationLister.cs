namespace gclo.Engine;

/// <summary>Lists the GitHub organizations a token can see.</summary>
public interface IOrganizationLister
{
    /// <summary>
    /// Returns the login names of organizations the token's user belongs to.
    /// May legitimately be empty for tokens without organization read access
    /// (fine-grained PATs, classic PATs missing read:org) — callers should still
    /// allow manual organization entry.
    /// </summary>
    Task<IReadOnlyList<string>> ListOrganizationsAsync(string token, CancellationToken cancellationToken = default);
}
