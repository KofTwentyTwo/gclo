namespace gclo.ViewModels;

/// <summary>
/// The single seam for where gclo keeps its per-user data (settings, accounts,
/// logs). Defaults to %LOCALAPPDATA%\gclo; the GCLO_DATA_DIR environment variable
/// overrides it so UI tests (and portable setups) can point the whole app at an
/// isolated directory without touching the real profile. Components that accept
/// an explicit directory (test seams) are unaffected — this only feeds defaults.
/// </summary>
public static class GcloPaths
{
    /// <summary>
    /// Root directory for gclo's per-user data: GCLO_DATA_DIR when set and
    /// non-empty, otherwise %LOCALAPPDATA%\gclo. Read on every access so a
    /// value set before launch always wins over any cached default.
    /// </summary>
    public static string DataRoot =>
        Environment.GetEnvironmentVariable("GCLO_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "gclo");
}
