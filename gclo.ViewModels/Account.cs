namespace gclo.ViewModels;

/// <summary>
/// A saved connection profile: which GitHub organization to sync, where its clones
/// live on disk, and how the sync runs. Metadata only — the access token is held
/// separately in an <see cref="ITokenVault"/> keyed by <see cref="Id"/> and is never
/// serialized alongside the account.
/// </summary>
public sealed record Account
{
    /// <summary>Stable identity: keys the vault entry and survives renames.</summary>
    public required Guid Id { get; init; }

    /// <summary>Display name; unique across accounts (case-insensitive).</summary>
    public required string Name { get; init; }

    /// <summary>Optional free-form note about what the account is for.</summary>
    public string Description { get; init; } = "";

    /// <summary>GitHub organization (or user) whose repositories are synced.</summary>
    public required string Organization { get; init; }

    /// <summary>Folder the sync targets; see <see cref="CreateOrgSubfolder"/>.</summary>
    public required string TargetRoot { get; init; }

    /// <summary>
    /// When true the effective sync target is <see cref="TargetRoot"/>\<see cref="Organization"/>
    /// rather than <see cref="TargetRoot"/> itself.
    /// </summary>
    public bool CreateOrgSubfolder { get; init; }

    /// <summary>Parallel clone/pull count used when syncing this account.</summary>
    public int MaxConcurrency { get; init; } = AppSettings.DefaultConcurrency;

    /// <summary>When the account's last sync finished, or null if it never ran.</summary>
    public DateTimeOffset? LastSyncUtc { get; init; }

    /// <summary>One-line outcome of the last sync, or null if it never ran.</summary>
    public string? LastSyncSummary { get; init; }
}
