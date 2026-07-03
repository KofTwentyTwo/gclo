using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace gclo.Services;

/// <summary>
/// Result of an update check. <see cref="AvailableVersion"/> is the newer version found,
/// or null when already up to date; <see cref="IsDownloaded"/> is true when that update is
/// already staged locally (e.g. by a previous session) and only needs a restart to apply;
/// <see cref="Error"/> is non-null when the check itself failed.
/// </summary>
public sealed record UpdateCheckResult(string? AvailableVersion, bool IsDownloaded, string? Error);

/// <summary>
/// Self-update via Velopack, with releases hosted on the app's public GitHub repository.
/// Updates only work in Velopack-installed builds: under F5, loose unpackaged builds, or
/// MSIX packages <see cref="IsSupported"/> is false. Every member is guarded — failures are
/// reported as strings and must never crash the app over something as optional as an update.
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/KofTwentyTwo/gclo";

    private UpdateManager? _manager;
    private UpdateInfo? _pendingUpdate;

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
                return new UpdateCheckResult(null, false, "Updates are only available in installed builds.");
            }
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(null, false, ex.Message);
        }

        try
        {
            UpdateInfo? update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            _pendingUpdate = update;
            if (update is null)
            {
                return new UpdateCheckResult(null, false, null);
            }

            bool downloaded = manager.UpdatePendingRestart?.Version == update.TargetFullRelease.Version;
            return new UpdateCheckResult(update.TargetFullRelease.Version.ToString(), downloaded, null);
        }
        catch (Exception ex)
        {
            _pendingUpdate = null;
            return new UpdateCheckResult(null, false, ex.Message);
        }
    }

    /// <summary>
    /// Downloads the update found by the last successful <see cref="CheckAsync"/>, applies it,
    /// and restarts the app. On success this never returns (the process exits to restart);
    /// otherwise it returns an error message. Never throws.
    /// </summary>
    /// <param name="progress">Optional download progress callback, called with 0-100.</param>
    /// <param name="cancellationToken">Optional token to abandon the download.</param>
    public async Task<string?> DownloadAndApplyAsync(Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        UpdateManager? manager = _manager;
        UpdateInfo? update = _pendingUpdate;
        if (manager is null || update is null)
        {
            return "No update is ready to install; check for updates first.";
        }

        try
        {
            await manager.DownloadUpdatesAsync(update, progress, cancellationToken).ConfigureAwait(false);
            manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
            return null;
        }
        catch (OperationCanceledException)
        {
            return "The update was canceled.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Created lazily so merely constructing the service can never fail; callers wrap this
    /// in try/catch and treat a throwing manager as "updates not supported".
    /// </summary>
    private UpdateManager GetManager() =>
        _manager ??= new UpdateManager(new GithubSource(RepoUrl, null, false));
}
