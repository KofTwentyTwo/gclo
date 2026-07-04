using System.Reflection;

namespace gclo.Engine;

/// <summary>
/// Human-readable build identity shared by the app's About dialog and the CLI's
/// version output: the semantic version plus the short git commit the build came
/// from, e.g. "0.1.0-beta.6 (9ac6c2b)" for releases or "0.1.0-dev (3a87f9e)" for
/// local builds.
/// </summary>
public static class BuildVersion
{
    private const int ShortHashLength = 9;

    /// <summary>Describes <paramref name="assembly"/>'s version and commit.</summary>
    public static string Describe(Assembly assembly)
        => Format(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString());

    /// <summary>
    /// Formats an informational version: the "+&lt;metadata&gt;" suffix that SourceLink
    /// appends becomes a parenthesized short commit hash.
    /// </summary>
    public static string Format(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return "unknown";
        }

        int plus = informationalVersion.IndexOf('+');
        if (plus < 0)
        {
            return informationalVersion;
        }

        string version = informationalVersion[..plus];
        string metadata = informationalVersion[(plus + 1)..];
        // Metadata may carry more than the sha (dot-separated); the hash is first.
        int dot = metadata.IndexOf('.');
        string hash = dot >= 0 ? metadata[..dot] : metadata;
        if (hash.Length > ShortHashLength)
        {
            hash = hash[..ShortHashLength];
        }

        return hash.Length == 0 ? version : $"{version} ({hash})";
    }
}
