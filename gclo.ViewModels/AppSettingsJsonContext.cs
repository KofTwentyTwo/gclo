/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json.Serialization;


namespace gclo.ViewModels;


/// <summary>
/// Source-generated serializer context so settings (de)serialization keeps working in
/// trimmed Release publishes without reflection warnings.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}
