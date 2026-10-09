namespace gclo.ViewModels;

/// <summary>
/// Thrown by <see cref="AccountsStore"/> when an operation failed AND the
/// compensating step that would have restored consistency between accounts.json and
/// the token vault failed too. The message describes the exact state left behind
/// and how the next operation reconciles it; it never contains a token.
/// <see cref="Exception.InnerException"/> is the original failure and
/// <see cref="CompensationException"/> the one that prevented recovery.
/// </summary>
public sealed class AccountConsistencyException : Exception
{
    /// <summary>The failure that occurred while undoing the partial operation.</summary>
    public Exception CompensationException { get; }

    /// <summary>Creates the exception from the original and compensating failures.</summary>
    public AccountConsistencyException(string message, Exception original, Exception compensation)
        : base(message, original)
    {
        CompensationException = compensation;
    }
}
