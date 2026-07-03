using System.Text.Json.Serialization;

namespace gclo.Cli;

/// <summary>One failed repository in the JSON summary.</summary>
internal sealed record SyncFailure(string Repo, string Error);

/// <summary>The single-line JSON document printed by 'gclo sync --json'.</summary>
internal sealed record SyncJsonResult(
    int Total,
    int Cloned,
    int Updated,
    int Failed,
    int Canceled,
    bool WasCanceled,
    IReadOnlyList<SyncFailure> Failures);

/// <summary>
/// Source-generated System.Text.Json serialization: no runtime reflection, so the
/// project stays free of trim/AOT warnings.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SyncJsonResult))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
