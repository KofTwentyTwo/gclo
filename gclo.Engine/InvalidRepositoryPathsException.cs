namespace gclo.Engine;

/// <summary>
/// Thrown instead of a raw libgit2 checkout error when a repository contains paths
/// that are legal in git but cannot be created on Windows (invalid characters,
/// reserved device names, trailing spaces/dots, case-only collisions).
/// </summary>
public sealed class InvalidRepositoryPathsException : Exception
{
    public IReadOnlyList<InvalidPathInfo> Paths { get; }

    public InvalidRepositoryPathsException(IReadOnlyList<InvalidPathInfo> paths)
        : base(BuildMessage(paths))
    {
        Paths = paths;
    }

    private static string BuildMessage(IReadOnlyList<InvalidPathInfo> paths)
    {
        var examples = string.Join("; ", paths.Take(3).Select(p => $"'{p.RepoPath}' ({p.Reason})"));
        var suffix = paths.Count > 3 ? $" and {paths.Count - 3} more" : "";
        return $"{paths.Count} path(s) in this repository cannot be created on Windows: {examples}{suffix}. " +
               "Nothing was checked out.";
    }
}
