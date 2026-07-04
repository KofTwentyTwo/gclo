using System.Collections.Concurrent;

namespace gclo.ViewModels;

/// <summary>
/// Thread-safe in-memory <see cref="ITokenVault"/>. Used by tests, and available for
/// scenarios where tokens must not outlive the process.
/// </summary>
public sealed class InMemoryVault : ITokenVault
{
    private readonly ConcurrentDictionary<Guid, string> _tokens = new();

    /// <inheritdoc/>
    public void Store(Guid accountId, string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        _tokens[accountId] = token;
    }

    /// <inheritdoc/>
    public string? TryRetrieve(Guid accountId)
        => _tokens.TryGetValue(accountId, out string? token) ? token : null;

    /// <inheritdoc/>
    public void Delete(Guid accountId)
        => _tokens.TryRemove(accountId, out _);
}
