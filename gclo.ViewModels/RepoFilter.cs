namespace gclo.ViewModels;

/// <summary>Which repository rows the workspace table shows.</summary>
public enum RepoFilter
{
    /// <summary>Every loaded repository.</summary>
    All,

    /// <summary>Rows with a git operation in flight (cloning or pulling).</summary>
    Active,

    /// <summary>Rows whose last run failed.</summary>
    Failed,

    /// <summary>Rows still queued and waiting for a worker.</summary>
    Pending,
}
