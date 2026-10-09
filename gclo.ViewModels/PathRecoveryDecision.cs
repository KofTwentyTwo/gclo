using gclo.Engine;

namespace gclo.ViewModels;

/// <summary>
/// What the user chose in the path-recovery UI for a repository whose tree has
/// Windows-invalid paths. Null (no decision) means the dialog was dismissed.
/// </summary>
public abstract record PathRecoveryDecision
{
    private PathRecoveryDecision() { }

    /// <summary>Rename/skip the offending paths and check the repository out on Windows.</summary>
    public sealed record Apply(PathRecovery Recovery) : PathRecoveryDecision;

    /// <summary>
    /// Leave the Windows copy alone and clone the repository inside WSL instead, where
    /// the paths are legal (#8). Only offered when <see cref="WorkspaceViewModel.IsWslCloneAvailable"/>.
    /// </summary>
    public sealed record CloneInWsl : PathRecoveryDecision;
}
