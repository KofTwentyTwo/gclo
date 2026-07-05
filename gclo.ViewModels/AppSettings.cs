using System.Text.Json;
using System.Text.Json.Serialization;

namespace gclo.ViewModels;

/// <summary>
/// User preferences persisted as JSON at settings.json under
/// <see cref="GcloPaths.DataRoot"/> (default %LOCALAPPDATA%\gclo, overridable via
/// GCLO_DATA_DIR). Uses Environment.GetFolderPath (not Windows.Storage.ApplicationData)
/// so the same path works whether the app runs packaged or unpackaged.
/// </summary>
public sealed class AppSettings
{
    public const int MinConcurrency = 1;
    public const int MaxConcurrency = 64;
    public const int DefaultConcurrency = 8;

    public const int MinSplashMilliseconds = 500;
    public const int MaxSplashMilliseconds = 30_000;
    public const int DefaultSplashMilliseconds = 5_000;

    /// <summary>
    /// Vault id under which the optional default GitHub token is stored. The token
    /// itself lives only in the <see cref="ITokenVault"/> (Credential Manager) —
    /// never in this settings file.
    /// </summary>
    public static readonly Guid DefaultTokenVaultId = new("d0f0e0c0-9c40-4a5e-8f3a-5b1e6f7a2c11");

    /// <summary>Folder pre-filled as the sync target. Empty means "no default".</summary>
    public string DefaultTargetFolder { get; set; } = "";

    /// <summary>Default parallel clone/pull count; clamped to 1..64 on load and save.</summary>
    public int DefaultMaxConcurrency { get; set; } = DefaultConcurrency;

    /// <summary>App theme: "System", "Light", or "Dark". Unknown values fall back to "System".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Whether the branded splash overlay shows at startup.</summary>
    public bool ShowSplashScreen { get; set; } = true;

    /// <summary>How long the splash stays up before fading, clamped to 500..30000 ms.</summary>
    public int SplashMilliseconds { get; set; } = DefaultSplashMilliseconds;

    private static string SettingsPath => Path.Combine(GcloPaths.DataRoot, "settings.json");

    /// <summary>
    /// Loads settings from disk. Never throws: a missing, corrupt, or unreadable file
    /// yields defaults, and loaded values are sanitized (concurrency clamped, theme validated).
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            string path = SettingsPath;
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize(
                    File.ReadAllText(path), AppSettingsJsonContext.Default.AppSettings);
                if (loaded is not null)
                {
                    loaded.Sanitize();
                    return loaded;
                }
            }
        }
        catch
        {
            // Settings must never prevent startup; fall through to defaults.
        }

        return new AppSettings();
    }

    /// <summary>
    /// Persists settings to disk, creating the directory if needed. Failures (locked file,
    /// read-only profile, full disk, ...) are swallowed: losing a preference write must
    /// never crash the app.
    /// </summary>
    public void Save()
    {
        try
        {
            Sanitize();
            string path = SettingsPath;
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(path, JsonSerializer.Serialize(this, AppSettingsJsonContext.Default.AppSettings));
        }
        catch
        {
            // Best effort only.
        }
    }


    private void Sanitize()
    {
        // The JSON deserializer can assign null despite the non-nullable annotation.
        DefaultTargetFolder ??= "";
        DefaultMaxConcurrency = Math.Clamp(DefaultMaxConcurrency, MinConcurrency, MaxConcurrency);
        SplashMilliseconds = Math.Clamp(SplashMilliseconds, MinSplashMilliseconds, MaxSplashMilliseconds);
        if (Theme is not ("System" or "Light" or "Dark"))
        {
            Theme = "System";
        }
    }
}

/// <summary>
/// Source-generated serializer context so settings (de)serialization keeps working in
/// trimmed Release publishes without reflection warnings.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}
