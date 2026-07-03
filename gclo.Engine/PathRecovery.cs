namespace gclo.Engine;

/// <summary>
/// A user-approved plan for materializing a repository whose tree contains
/// Windows-invalid paths: rename some paths, omit others. Applied by
/// <see cref="IGitClient.ApplyRecoveryAsync"/> instead of a normal checkout.
/// </summary>
/// <param name="SegmentRenames">
/// Maps an original repository path (forward-slash separated) to its full
/// replacement repository path (already joined, forward slashes). A directory
/// mapping relocates its whole subtree.
/// </param>
/// <param name="SkippedPaths">
/// Repository paths (files or whole directories) to omit from the working tree.
/// </param>
public sealed record PathRecovery(
    IReadOnlyDictionary<string, string> SegmentRenames,
    IReadOnlySet<string> SkippedPaths);
