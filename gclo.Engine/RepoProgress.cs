namespace gclo.Engine;

/// <summary>A progress snapshot for one repository.</summary>
/// <param name="RepoName">Repository name.</param>
/// <param name="Status">Current lifecycle state.</param>
/// <param name="Error">Human-readable failure reason when <see cref="Status"/> is <see cref="SyncStatus.Failed"/>.</param>
/// <param name="Percent">Object-transfer completion in [0,1] while cloning, when known.</param>
/// <param name="InvalidPaths">
/// The offending paths when the repository failed with
/// <see cref="InvalidRepositoryPathsException"/>; null for every other failure.
/// Lets consumers offer path recovery (rename/skip) without re-running the sync.
/// </param>
public sealed record RepoProgress(
    string RepoName,
    SyncStatus Status,
    string? Error = null,
    double? Percent = null,
    IReadOnlyList<InvalidPathInfo>? InvalidPaths = null);
