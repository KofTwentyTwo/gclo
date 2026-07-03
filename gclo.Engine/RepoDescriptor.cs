namespace gclo.Engine;

/// <summary>A repository discovered in the GitHub organization.</summary>
/// <param name="Name">Repository name (unique within the org; used as the local folder name).</param>
/// <param name="CloneUrl">HTTPS clone URL.</param>
/// <param name="DefaultBranch">Default branch name; null for empty repositories.</param>
/// <param name="IsArchived">Whether the repository is archived (still cloneable, read-only).</param>
public sealed record RepoDescriptor(string Name, string CloneUrl, string? DefaultBranch, bool IsArchived);
