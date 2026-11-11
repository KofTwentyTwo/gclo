/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json.Serialization;


namespace gclo.Engine;


/// <summary>
/// A repository as the Gitea API describes it, as far as gclo reads it. The clone
/// address the server gives is deliberately not read: gclo builds it from the
/// address the account names (see <see cref="GiteaApi.CloneUrl"/>).
/// </summary>
/// <param name="Name">The repository's name.</param>
/// <param name="Owner">The account that owns it.</param>
/// <param name="DefaultBranch">Its default branch.</param>
/// <param name="Archived">Whether it is archived.</param>
/// <param name="Empty">Whether it has no commits.</param>
internal sealed record GiteaRepository(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("owner")] GiteaAccount? Owner,
    [property: JsonPropertyName("default_branch")] string? DefaultBranch,
    [property: JsonPropertyName("archived")] bool Archived,
    [property: JsonPropertyName("empty")] bool Empty);
