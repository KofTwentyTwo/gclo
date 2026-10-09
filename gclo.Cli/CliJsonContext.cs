/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json.Serialization;


namespace gclo.Cli;


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
