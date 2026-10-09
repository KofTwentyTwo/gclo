namespace gclo.Engine;

/// <summary>Why GitHub refused a request, in the terms a caller can act on.</summary>
public enum GitHubAccessKind
{
    /// <summary>The token was rejected (401): expired, revoked, or malformed.</summary>
    Unauthorized,

    /// <summary>The token is valid but may not see the target (an ungranted organization, missing SSO).</summary>
    Forbidden,

    /// <summary>A primary or secondary rate limit, or temporary abuse/login throttling: retry later.</summary>
    RateLimited,

    /// <summary>No organization or user account with that login is visible to the token.</summary>
    NotFound,
}

/// <summary>
/// A GitHub API refusal translated into an actionable message. Derives from
/// <see cref="InvalidOperationException"/> so every existing catch keeps working;
/// <see cref="Kind"/> lets a caller tell "fix your token" from "wait and retry"
/// without parsing the message — the CLI maps it to distinct exit codes.
/// </summary>
public sealed class GitHubAccessException : InvalidOperationException
{
    /// <summary>The category of refusal.</summary>
    public GitHubAccessKind Kind { get; }

    /// <summary>Creates the exception with its category, user-facing message, and the Octokit cause.</summary>
    public GitHubAccessException(GitHubAccessKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }
}
