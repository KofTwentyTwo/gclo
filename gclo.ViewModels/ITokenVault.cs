namespace gclo.ViewModels;

/// <summary>
/// Secure storage for account access tokens, keyed by account id. Tokens must never
/// reach accounts.json or the activity log; implementations keep them in a platform
/// secret store (<see cref="CredentialManagerVault"/>) or in process memory
/// (<see cref="InMemoryVault"/>).
/// </summary>
public interface ITokenVault
{
    /// <summary>
    /// Stores the token for an account, overwriting any existing entry. Throws on
    /// failure — silently losing a token would leave the account unusable without
    /// any warning to the user.
    /// </summary>
    void Store(Guid accountId, string token);

    /// <summary>The stored token, or null when the vault has no entry for the account.</summary>
    string? TryRetrieve(Guid accountId);

    /// <summary>Removes the account's token. Deleting an absent entry is a no-op.</summary>
    void Delete(Guid accountId);
}
