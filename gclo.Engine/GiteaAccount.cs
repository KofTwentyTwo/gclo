/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json.Serialization;


namespace gclo.Engine;


/// <summary>
/// A user or an organization as the Gitea API describes it, as far as gclo reads it.
/// A user (and the owner of a repository) carries its name as <c>login</c>; an
/// organization in the list of a user's organizations carries it as <c>username</c>.
/// </summary>
/// <param name="Login">The <c>login</c> field.</param>
/// <param name="Username">The <c>username</c> field.</param>
internal sealed record GiteaAccount(
    [property: JsonPropertyName("login")] string? Login,
    [property: JsonPropertyName("username")] string? Username)
{
   /// <summary>The account's name, whichever field carries it; null when neither does.</summary>
   [JsonIgnore]
   public string? Name => !string.IsNullOrWhiteSpace(Login) ? Login : !string.IsNullOrWhiteSpace(Username) ? Username : null;
}
