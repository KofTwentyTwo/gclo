/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json.Serialization;


namespace gclo.Engine;


/// <summary>The document a Gitea server answers a refused request with.</summary>
/// <param name="Message">The server's reason.</param>
internal sealed record GiteaRefusal([property: JsonPropertyName("message")] string? Message);
