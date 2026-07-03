namespace gclo.Engine;

/// <summary>Lifecycle of a single repository within an org sync.</summary>
public enum SyncStatus
{
    Queued,
    Cloning,
    Pulling,
    Done,
    Failed,
    Canceled,
}
