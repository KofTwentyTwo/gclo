namespace gclo.Engine;

/// <summary>Aggregate outcome of an organization sync.</summary>
/// <param name="Total">Number of repositories the sync set out to process.</param>
/// <param name="Cloned">Repositories cloned for the first time.</param>
/// <param name="Updated">Existing repositories fetched and fast-forwarded.</param>
/// <param name="Failed">Repositories that failed; a failure never aborts the others.</param>
/// <param name="Canceled">Repositories canceled before or during processing.</param>
/// <param name="WasCanceled">Whether the run was cut short by cancellation.</param>
public sealed record SyncSummary(int Total, int Cloned, int Updated, int Failed, int Canceled, bool WasCanceled);
