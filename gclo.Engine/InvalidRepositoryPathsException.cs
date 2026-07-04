namespace gclo.Engine;

/// <summary>
/// Thrown instead of a raw libgit2 checkout error when a repository contains paths
/// that are legal in git but cannot be created on Windows (invalid characters,
/// reserved device names, trailing spaces/dots, case-only collisions).
/// </summary>
public sealed class InvalidRepositoryPathsException : Exception
{
    /// <summary>The offending paths, one entry per invalid path.</summary>
    public IReadOnlyList<InvalidPathInfo> Paths { get; }

    /// <summary>Creates the exception from the validator's findings.</summary>
    public InvalidRepositoryPathsException(IReadOnlyList<InvalidPathInfo> paths)
        : base(BuildMessage(paths))
    {
        Paths = paths;
    }

    private static string BuildMessage(IReadOnlyList<InvalidPathInfo> paths)
    {
        var examples = string.Join("; ", paths.Take(3).Select(p => $"'{p.RepoPath}' ({p.Reason})"));
        var suffix = paths.Count > 3 ? $" and {paths.Count - 3} more" : "";
        string subject = paths.Count == 1 ? "1 path" : $"{paths.Count} paths";
        return $"{subject} in this repository cannot be created on Windows: {examples}{suffix}. " +
               "Nothing was checked out.";
    }
}
