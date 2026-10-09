/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json.Serialization;


namespace gclo.ViewModels;


/// <summary>
/// Source-generated serializer context so account (de)serialization keeps working in
/// trimmed Release publishes without reflection warnings.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<Account>))]
internal sealed partial class AccountsJsonContext : JsonSerializerContext
{
}
