namespace gclo.Engine;

/// <summary>Aggregate outcome of an organization sync.</summary>
public sealed record SyncSummary(int Total, int Cloned, int Updated, int Failed, int Canceled, bool WasCanceled);
