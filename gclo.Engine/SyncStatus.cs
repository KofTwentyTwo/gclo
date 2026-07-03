namespace gclo.Engine;

/// <summary>Lifecycle of a single repository within an org sync.</summary>
public enum SyncStatus
{
    /// <summary>Discovered and waiting for a worker.</summary>
    Queued,

    /// <summary>Being cloned for the first time.</summary>
    Cloning,

    /// <summary>Existing local repository being fetched and fast-forwarded.</summary>
    Pulling,

    /// <summary>Finished successfully.</summary>
    Done,

    /// <summary>Failed; the progress report carries the reason.</summary>
    Failed,

    /// <summary>Stopped by cancellation before or during processing.</summary>
    Canceled,
}
