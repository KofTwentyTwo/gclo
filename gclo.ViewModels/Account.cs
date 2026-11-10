/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json.Serialization;

namespace gclo.ViewModels;


/// <summary>
/// A saved connection profile: which GitHub organization to sync, where its clones
/// live on disk, and how the sync runs. Metadata only — the access token is held
/// separately in an <see cref="ITokenVault"/> (keyed by <see cref="Id"/>, or the
/// default token when <see cref="TokenSource"/> says so) and is never serialized
/// alongside the account.
/// </summary>
public sealed record Account
{
   /// <summary>Stable identity: keys the vault entry and survives renames.</summary>
   public required Guid Id { get; init; }

   /// <summary>
   /// Where the token comes from: this account's own vault entry (the default, and
   /// omitted from accounts.json so files written before this field exist stay
   /// byte-identical) or the default token from Settings, resolved at use time by
   /// <see cref="AccountTokenResolver"/>.
   /// </summary>
   [JsonConverter(typeof(JsonStringEnumConverter<TokenSource>))]
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
   public TokenSource TokenSource { get; init; } = TokenSource.Own;

   /// <summary>True when the account resolves to the default token from Settings.</summary>
   [JsonIgnore]
   public bool UsesDefaultToken => TokenSource == TokenSource.Default;

   /// <summary>Display name; unique across accounts (case-insensitive).</summary>
   public required string Name { get; init; }

   /// <summary>
   /// Optional free-form note about what the account is for. Never null: a file
   /// that has no such field is read as an empty note.
   /// </summary>
   public string Description
   {
      get;
      init => field = value ?? "";
   } = "";

   /// <summary>GitHub organization (or user) whose repositories are synced.</summary>
   public required string Organization { get; init; }

   /// <summary>Folder the sync targets; see <see cref="CreateOrgSubfolder"/>.</summary>
   public required string TargetRoot { get; init; }

   /// <summary>
   /// When true the effective sync target is <see cref="TargetRoot"/>\<see cref="Organization"/>
   /// rather than <see cref="TargetRoot"/> itself.
   /// </summary>
   public bool CreateOrgSubfolder { get; init; }

   /// <summary>
   /// Parallel clone/pull count used when syncing this account. Always between
   /// <see cref="AppSettings.MinConcurrency"/> and <see cref="AppSettings.MaxConcurrency"/>,
   /// so every user of the account gets a usable number. A value below the minimum
   /// is not a setting anyone chose (an accounts.json edited by hand, or one without
   /// the field, which is read as zero): it becomes the default. A value above the
   /// maximum becomes the maximum.
   /// </summary>
   public int MaxConcurrency
   {
      get;
      init => field = value < AppSettings.MinConcurrency
          ? AppSettings.DefaultConcurrency
          : Math.Min(value, AppSettings.MaxConcurrency);
   } = AppSettings.DefaultConcurrency;

   /// <summary>When the account's last sync finished, or null if it never ran.</summary>
   public DateTimeOffset? LastSyncUtc { get; init; }

   /// <summary>One-line outcome of the last sync, or null if it never ran.</summary>
   public string? LastSyncSummary { get; init; }
}
