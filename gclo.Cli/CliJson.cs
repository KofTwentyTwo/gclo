using System.Text.Json.Serialization;

namespace gclo.Cli;

/// <summary>One Windows-invalid path behind a failure, as the validator reported it.</summary>
internal sealed record SyncInvalidPath(string Path, string Reason, string? SuggestedName);

/// <summary>
/// One failed repository in the JSON summary. <see cref="InvalidPaths"/> is present
/// (non-null) only when the failure was Windows path validation — the full list, not
/// the three-path excerpt the message carries.
/// </summary>
internal sealed record SyncFailure(string Repo, string Error, IReadOnlyList<SyncInvalidPath>? InvalidPaths = null);

/// <summary>
/// One repository that '--sanitize-paths' checked out with renames and skips: it
/// counts as cloned, but its working tree is incomplete by <see cref="Skipped"/>
/// paths, which a mirroring pipeline needs to know.
/// </summary>
internal sealed record SyncSanitized(string Repo, int Renamed, int Skipped, IReadOnlyList<string> SkippedPaths);

/// <summary>
/// The single-line JSON document printed by 'gclo sync --json', and the last line of
/// 'gclo sync --json-lines'. <see cref="Type"/> is always "summary" so a line reader
/// can tell it from progress lines.
/// </summary>
internal sealed record SyncJsonResult(
    string Type,
    int Total,
    int Cloned,
    int Updated,
    int Failed,
    int Canceled,
    bool WasCanceled,
    IReadOnlyList<SyncFailure> Failures,
    IReadOnlyList<SyncSanitized> Sanitized);

/// <summary>One status transition in 'gclo sync --json-lines'; <see cref="Type"/> is always "progress".</summary>
internal sealed record SyncProgressLine(string Type, string Repo, string Status, string? Error);

/// <summary>One repository of 'gclo sync --dry-run --json': what the run would do with it.</summary>
internal sealed record DryRunEntry(string Repo, string Action);

/// <summary>One repository in the JSON listing of 'gclo repos --json'.</summary>
internal sealed record RepoSummary(string Name, string? DefaultBranch, bool Archived, string CloneUrl);

/// <summary>
/// One saved account in the JSON listing of 'gclo accounts --json'.
/// <see cref="LastSync"/> is the UTC time of the last completed sync, or null
/// when the account has never synced; <see cref="Id"/> is the key of its
/// Credential Manager entry ('gclo:account:&lt;id&gt;').
/// </summary>
internal sealed record AccountSummary(
    string Id,
    string Name,
    string Description,
    string Organization,
    string TargetRoot,
    bool CreateOrgSubfolder,
    int MaxConcurrency,
    DateTimeOffset? LastSync,
    string? LastSyncSummary);

/// <summary>
/// Source-generated System.Text.Json serialization: no runtime reflection, so the
/// project stays free of trim/AOT warnings.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SyncJsonResult))]
[JsonSerializable(typeof(SyncProgressLine))]
[JsonSerializable(typeof(IReadOnlyList<DryRunEntry>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<RepoSummary>))]
[JsonSerializable(typeof(IReadOnlyList<AccountSummary>))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
