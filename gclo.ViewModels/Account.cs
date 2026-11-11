/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using gclo.Engine;

namespace gclo.ViewModels;


/// <summary>
/// A saved connection profile: which organization to sync (on GitHub, or on a
/// Gitea server), where its clones live on disk, and how the sync runs. Metadata
/// only — the access token is held separately in an <see cref="ITokenVault"/> (keyed
/// by <see cref="TokenEntryId"/>, or the default token when <see cref="TokenSource"/>
/// says so) and is never serialized alongside the account.
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

   /// <summary>
   /// The kind of forge the account is on, as it is stored: its name, and nothing
   /// (null) for GitHub, so a GitHub account is written as it always was. Stored as
   /// text and kept as it was read: a name this version does not know (a forge a
   /// later version added) makes the one account unusable here, not the file
   /// unreadable, and is written back unchanged. Set when the account is added and
   /// not changed afterwards: see <see cref="AccountsStore.Save"/>.
   /// </summary>
   [JsonPropertyName("Forge")]
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public string? ForgeName { get; init; }

   /// <summary>
   /// The kind of forge the account is on. GitHub is the account that names no
   /// forge, and only that one: it is how every version has written a GitHub
   /// account. For a name this version does not know it is none of the kinds
   /// (<see cref="IsOnAKnownForge"/> is false): never GitHub, so nothing takes such
   /// an account for a GitHub account.
   /// </summary>
   [JsonIgnore]
   public ForgeKind Forge
   {
      get => ForgeName is null ? ForgeKind.GitHub
          : string.Equals(ForgeName, nameof(ForgeKind.Gitea), StringComparison.OrdinalIgnoreCase) ? ForgeKind.Gitea
          : s_noKnownForge;
      init => ForgeName = value == ForgeKind.GitHub ? null : value.ToString();
   }

   /// <summary>False when the account names a forge this version of gclo does not know.</summary>
   [JsonIgnore]
   public bool IsOnAKnownForge => Forge != s_noKnownForge;

   /// <summary>
   /// The address of the account's server, for a forge that has no one well-known
   /// host (Gitea); null for GitHub, and then left out of the file. It is where
   /// the account's token is sent, so it is set when the account is added and not
   /// changed afterwards.
   /// </summary>
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public string? Server { get; init; }

   /// <summary>
   /// The vault entry that holds the account's own token: the account's id for a
   /// GitHub account, and for any other forge an id made from the account's id, its
   /// forge and its server, so a token is found only by the account it was saved
   /// for, at the place it was saved for (see <see cref="AccountTokenEntry"/>).
   /// </summary>
   [JsonIgnore]
   public Guid TokenEntryId => AccountTokenEntry.For(this);

   /// <summary>
   /// What the file says about the account that this version of gclo does not know
   /// (a field a later version added). Kept, and written back as it was read, so
   /// that changing an account here does not take away what a later version put
   /// there. Null when there is nothing of the kind. <see cref="AccountsStore.Save"/>
   /// keeps what the file has, whatever the record it is given says here.
   /// </summary>
   [JsonExtensionData]
   public IDictionary<string, JsonElement>? FieldsOfALaterVersion { get; set; }

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

   /// <summary>The organization (or user account) whose repositories are synced.</summary>
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



   /// <summary>What <see cref="Forge"/> is for a forge name this version does not know: a value that is none of the kinds.</summary>
   private static readonly ForgeKind s_noKnownForge = (ForgeKind)(-1);



   /// <summary>
   /// Where the account lives, or the reason its forge and server cannot be used as
   /// they are stored (a file edited by hand, or a forge this version does not know).
   /// </summary>
   public bool TryGetEndpoint([NotNullWhen(true)] out ForgeEndpoint? endpoint, [NotNullWhen(false)] out string? reason)
   {
      if(!IsOnAKnownForge)
      {
         endpoint = null;
         reason = "it is on a forge this version of gclo does not know";
         return false;
      }
      return ForgeEndpoint.TryCreate(Forge, Server, out endpoint, out reason);
   }



   /// <summary>
   /// How a message or a listing names the place the account is on: "GitHub", or the
   /// host of its server. When what is stored cannot be used, the kind of forge
   /// alone: such an address is not repeated, it may hold a password or a token.
   /// </summary>
   public string DescribePlace()
       => TryGetEndpoint(out ForgeEndpoint? endpoint, out _) ? endpoint.DisplayName
           : IsOnAKnownForge ? $"{Forge}, at an address that cannot be used"
           : "a forge this version of gclo does not know";



   /// <summary>
   /// What to do about the account when it cannot be used as it is saved, as the
   /// sentence that ends such a message. <paramref name="remove"/> is how the caller
   /// names removing an account ("Delete" in the app, "Remove" on the command line).
   /// </summary>
   public string AdviceWhenUnusable(string remove)
       => IsOnAKnownForge ? $"{remove} the account and add it again." : "Use the version of gclo that added it.";
}
