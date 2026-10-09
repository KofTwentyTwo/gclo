using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using gclo.Engine;
using Velopack;
using Velopack.Sources;

namespace gclo.Services;

/// <summary>
/// Result of an update check. <see cref="AvailableVersion"/> is the newer version found,
/// or null when already up to date; <see cref="Error"/> is non-null when the check itself failed.
/// </summary>
public sealed record UpdateCheckResult(string? AvailableVersion, string? Error);

/// <summary>
/// Self-update via Velopack, with releases hosted on the app's public GitHub repository.
/// Updates only work in Velopack-installed builds: under F5, loose unpackaged builds, or
/// MSIX packages <see cref="IsSupported"/> is false. Every member is guarded — failures are
/// reported as strings and must never crash the app over something as optional as an update.
/// Every transition of the flow is written to the activity log (#40); the version strings
/// are the only payload, never a URL with credentials or a token.
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/KofTwentyTwo/gclo";

    private readonly IActivityLog _log;
    private UpdateManager? _manager;
    private UpdateInfo? _pendingUpdate;

    /// <summary>Creates the service; <paramref name="log"/> receives every update-flow transition.</summary>
    public UpdateService(IActivityLog? log = null)
    {
        _log = log ?? new NullActivityLog();
    }

    /// <summary>True when the app was installed by Velopack and can check for and apply updates.</summary>
    public bool IsSupported
    {
        get
        {
            try
            {
                return GetManager().IsInstalled;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>The installed version as Velopack sees it, or null outside installed builds.</summary>
    public string? CurrentVersion
    {
        get
        {
            try
            {
                return GetManager().CurrentVersion?.ToString();
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Checks GitHub releases for a newer version and remembers it for
    /// <see cref="DownloadAndApplyAsync"/>. Never throws: failures come back in
    /// <see cref="UpdateCheckResult.Error"/>.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync()
    {
        UpdateManager manager;
        try
        {
            manager = GetManager();
            if (!manager.IsInstalled)
            {
                _log.Info("Update check skipped: not an installed build.");
                return new UpdateCheckResult(null, "Updates are only available in installed builds.");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Update check failed before contacting GitHub: {ex.Message}", ex);
            return new UpdateCheckResult(null, ex.Message);
        }

        string current = manager.CurrentVersion?.ToString() ?? "unknown";
        _log.Info($"Update check started (installed v{current}).");
        try
        {
            UpdateInfo? update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            _pendingUpdate = update;
            if (update is null)
            {
                _log.Info($"Up to date: v{current} is the latest on this channel.");
                return new UpdateCheckResult(null, null);
            }

            string available = update.TargetFullRelease.Version.ToString();
            _log.Info($"Update available: v{available} (installed v{current}).");
            return new UpdateCheckResult(available, null);
        }
        catch (Exception ex)
        {
            _pendingUpdate = null;
            _log.Error($"Update check failed: {ex.Message}", ex);
            return new UpdateCheckResult(null, ex.Message);
        }
    }

    /// <summary>
    /// Downloads the update found by the last successful <see cref="CheckAsync"/>, applies it,
    /// and restarts the app, reporting download completion (0-100) through
    /// <paramref name="progress"/> when given. On success this never returns (the process
    /// exits to restart); otherwise it returns an error message. Never throws.
    /// </summary>
    public async Task<string?> DownloadAndApplyAsync(IProgress<int>? progress = null)
    {
        UpdateManager? manager = _manager;
        UpdateInfo? update = _pendingUpdate;
        if (manager is null || update is null)
        {
            _log.Error("Update apply requested with no update ready; check for updates first.");
            return "No update is ready to install; check for updates first.";
        }

        string version = update.TargetFullRelease.Version.ToString();
        _log.Info($"Downloading update v{version}.");
        try
        {
            await manager.DownloadUpdatesAsync(
                update, percent => progress?.Report(percent), CancellationToken.None).ConfigureAwait(false);
            _log.Info($"Update v{version} downloaded; restarting to apply it.");
            manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
            return null;
        }
        catch (Exception ex)
        {
            _log.Error($"Update to v{version} failed: {ex.Message}", ex);
            return ex.Message;
        }
    }

    /// <summary>
    /// Created lazily so merely constructing the service can never fail; callers wrap this
    /// in try/catch and treat a throwing manager as "updates not supported".
    /// A prerelease install (semver has a '-suffix') runs on the dev channel, whose
    /// packages are attached to GitHub *prerelease* releases — GithubSource must be
    /// told to look at prereleases or dev installs can never find any update at all.
    /// Stable installs keep prereleases out of consideration; Velopack's channel
    /// filtering then matches packages to the installed channel either way.
    /// </summary>
    private UpdateManager GetManager()
    {
        if (_manager is null)
        {
            bool prerelease = BuildVersion
                .Describe(Assembly.GetExecutingAssembly())
                .Contains('-');
            _manager = new UpdateManager(new GithubSource(RepoUrl, null, prerelease));
        }
        return _manager;
    }
}
