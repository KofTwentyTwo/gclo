namespace gclo.Engine;

/// <summary>One repository path that cannot be created on a Windows file system.</summary>
/// <param name="RepoPath">Path inside the repository, forward-slash separated.</param>
/// <param name="Reason">Human-readable reason the path is invalid on Windows.</param>
/// <param name="SuggestedName">A sanitized replacement for the offending segment, when one can be derived.</param>
public sealed record InvalidPathInfo(string RepoPath, string Reason, string? SuggestedName);
