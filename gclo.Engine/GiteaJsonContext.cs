/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json.Serialization;


namespace gclo.Engine;


/// <summary>
/// Source-generated JSON reading for the Gitea API's answers: no reflection, so the
/// engine stays trimmable, and no JSON package beyond the framework's.
/// </summary>
[JsonSerializable(typeof(List<GiteaRepository?>))]
[JsonSerializable(typeof(List<GiteaAccount?>))]
[JsonSerializable(typeof(GiteaAccount))]
[JsonSerializable(typeof(GiteaRefusal))]
internal sealed partial class GiteaJsonContext : JsonSerializerContext;
