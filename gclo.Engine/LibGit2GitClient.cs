using System.Text.Json;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace gclo.Engine;

/// <summary>Git operations implemented with LibGit2Sharp.</summary>
public sealed class LibGit2GitClient : IGitClient
{
    /// <summary>
    /// Local git config flag set when a clone fetched successfully but its tree failed
    /// Windows path validation, so nothing was ever checked out. Cleared once a working
    /// tree is materialized (normal checkout after upstream fixed the paths, or
    /// <see cref="ApplyRecoveryAsync"/>).
    /// </summary>
    private const string CheckoutPendingKey = "gclo.checkoutpending";

    /// <summary>File name (under .git) of the persisted <see cref="PathRecovery"/>.</summary>
    private const string RecoveryFileName = "gclo-recovery.json";

    private static readonly JsonSerializerOptions RecoveryJsonOptions = new() { WriteIndented = true };

    /// <inheritdoc/>
    public bool IsValidRepository(string path)
        => Directory.Exists(path) && Repository.IsValid(path);

    // LongRunning: each git operation blocks a thread for its whole duration; on the
    // thread pool, MaxConcurrency operations would starve the pool and delay ramp-up.
    /// <inheritdoc/>
    public Task CloneAsync(string url, string path, string token, Action<double>? onProgress, CancellationToken cancellationToken)
        => Task.Factory.StartNew(
            () => Clone(url, path, token, onProgress, cancellationToken),
            cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <inheritdoc/>
    public Task FetchAndPullAsync(string path, string token, CancellationToken cancellationToken)
        => Task.Factory.StartNew(
            () => FetchAndPull(path, token, cancellationToken),
            cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <inheritdoc/>
    public Task ApplyRecoveryAsync(string path, PathRecovery recovery, CancellationToken cancellationToken)
        => Task.Factory.StartNew(
            () => ApplyRecovery(path, recovery, cancellationToken),
            cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void Clone(string url, string path, string token, Action<double>? onProgress, CancellationToken ct)
    {
        bool existedBefore = Directory.Exists(path);

        var options = new CloneOptions
        {
            // Two-phase: fetch only, then checkout. On Windows the gap is used to
            // set core.longpaths and validate every tree path against Windows rules —
            // a repo that is fine on Linux must fail there with a structured error,
            // not a libgit2 one. On other platforms phase two is a plain checkout.
            Checkout = false,
        };
        options.FetchOptions.CredentialsProvider = MakeCredentialsProvider(token);
        int lastPercent = -1;
        options.FetchOptions.OnTransferProgress = tp =>
        {
            if (tp.TotalObjects > 0)
            {
                // libgit2 fires this per network read / indexed object — thousands of
                // times per clone. Forward only whole-percent changes so the UI
                // dispatcher is not flooded.
                int percent = (int)(100L * tp.ReceivedObjects / tp.TotalObjects);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    onProgress?.Invoke(percent / 100.0);
                }
            }
            return !ct.IsCancellationRequested;
        };
        try
        {
            Repository.Clone(url, path, options);

            using var repo = new Repository(path);
            if (OperatingSystem.IsWindows())
            {
                // Lifts the 260-character path limit before anything touches the
                // working tree; libgit2 honors it for checkout.
                repo.Config.Set("core.longpaths", true, ConfigurationLevel.Local);
            }

            var tip = repo.Head.Tip;
            if (tip is null)
            {
                return; // empty repository — nothing to check out
            }

            if (OperatingSystem.IsWindows())
            {
                var invalidPaths = WindowsPathValidator.Validate(tip.Tree);
                if (invalidPaths.Count > 0)
                {
                    // Keep the fetched repo: all objects are already downloaded, so
                    // ApplyRecoveryAsync can materialize a sanitized working tree without
                    // touching the network. The marker tells FetchAndPull that this
                    // repository was never checked out.
                    repo.Config.Set(CheckoutPendingKey, true, ConfigurationLevel.Local);
                    throw new InvalidRepositoryPathsException(invalidPaths);
                }
            }

            ct.ThrowIfCancellationRequested();
            Commands.Checkout(repo, repo.Head, new CheckoutOptions
            {
                CheckoutModifiers = CheckoutModifiers.Force, // materialize the fresh working tree
            });
        }
        catch (Exception ex)
        {
            // Don't leave a half-cloned directory behind: the next run would treat a
            // partial checkout as "exists locally" and try to pull it. Invalid-path
            // failures are the deliberate exception — the fetched repo is kept (with
            // the pending marker set above) so recovery needs no re-download.
            if (!existedBefore && ex is not InvalidRepositoryPathsException)
            {
                TryDeleteDirectory(path);
            }

            if (ex is UserCancelledException && ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            throw;
        }
    }

    private static void FetchAndPull(string path, string token, CancellationToken ct)
    {
        using var repo = new Repository(path);

        if (OperatingSystem.IsWindows())
        {
            // Idempotent; also covers repositories that were cloned by other tools.
            repo.Config.Set("core.longpaths", true, ConfigurationLevel.Local);
        }

        var remote = repo.Network.Remotes["origin"]
            ?? throw new InvalidOperationException("Repository has no 'origin' remote.");

        var fetchOptions = new FetchOptions
        {
            CredentialsProvider = MakeCredentialsProvider(token),
            OnTransferProgress = _ => !ct.IsCancellationRequested,
        };

        var refSpecs = remote.FetchRefSpecs.Select(s => s.Specification).ToList();
        try
        {
            Commands.Fetch(repo, remote.Name, refSpecs, fetchOptions, logMessage: null);
        }
        catch (UserCancelledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        ct.ThrowIfCancellationRequested();

        // Repositories whose clone hit Windows-invalid paths never had a checkout;
        // they carry a pending marker (and, once the user chose renames/skips, a
        // persisted recovery). Both take a dedicated path — their working tree is
        // materialized manually, never by a merge checkout. Every other repository
        // takes the normal fetch + fast-forward pull below.
        string recoveryFile = GetRecoveryFilePath(repo);
        if (File.Exists(recoveryFile))
        {
            // A recovery-managed repo stays recovery-managed: re-materialize the new
            // tip through the stored mapping. Freshly-invalid paths the mapping does
            // not cover surface as a typed failure listing the effective paths.
            AdvanceHeadToTrackedTip(repo);
            ApplyRecoveryCore(repo, LoadRecovery(recoveryFile), ct);
            return;
        }

        if (IsCheckoutPending(repo))
        {
            CompletePendingCheckout(repo, ct);
            return;
        }

        if (repo.Info.IsHeadDetached)
        {
            throw new InvalidOperationException("HEAD is detached; fetched, but nothing was merged.");
        }

        if (repo.Info.IsHeadUnborn)
        {
            // Empty clone — or a clone that was killed before its first checkout ran.
            // If the remote branch exists now, materialize it instead of silently
            // reporting an empty working tree as up to date forever.
            MaterializeUnbornHead(repo, ct);
            return;
        }

        var tracked = repo.Head.TrackedBranch;
        if (tracked?.Tip is null)
        {
            // No upstream configured: fetch is all we can do.
            return;
        }

        if (repo.Head.Tip?.Sha == tracked.Tip.Sha)
        {
            return; // already up to date
        }

        // Incoming commits can introduce Windows-invalid paths just like a clone can.
        if (OperatingSystem.IsWindows())
        {
            var invalidIncoming = WindowsPathValidator.Validate(tracked.Tip.Tree);
            if (invalidIncoming.Count > 0)
            {
                throw new InvalidRepositoryPathsException(invalidIncoming);
            }
        }

        // Fast-forward only: a mirror tool must never manufacture merge commits.
        // Diverged local history surfaces as NonFastForwardException -> Failed with a clear message.
        var signature = new Signature("gclo", "gclo@localhost", DateTimeOffset.Now);
        try
        {
            repo.Merge(tracked, signature, new MergeOptions
            {
                FastForwardStrategy = FastForwardStrategy.FastForwardOnly,
                CheckoutNotifyFlags = CheckoutNotifyFlags.Updated,
                OnCheckoutNotify = (_, _) => !ct.IsCancellationRequested,
            });
        }
        catch (UserCancelledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    /// <summary>
    /// HEAD symbolically points at a branch with no commits. If the fetch brought that
    /// branch's remote counterpart, create the local branch there, wire its upstream,
    /// and check it out to populate the working tree.
    /// </summary>
    private static void MaterializeUnbornHead(Repository repo, CancellationToken ct)
    {
        const string prefix = "refs/heads/";
        string targetRef = repo.Refs.Head.TargetIdentifier;
        if (!targetRef.StartsWith(prefix, StringComparison.Ordinal))
        {
            return;
        }

        string branchName = targetRef[prefix.Length..];
        var remoteBranch = repo.Branches["origin/" + branchName];
        if (remoteBranch?.Tip is null)
        {
            return; // the remote is still empty; nothing to pull
        }

        if (OperatingSystem.IsWindows())
        {
            var invalidPaths = WindowsPathValidator.Validate(remoteBranch.Tip.Tree);
            if (invalidPaths.Count > 0)
            {
                throw new InvalidRepositoryPathsException(invalidPaths);
            }
        }

        repo.Refs.Add(targetRef, remoteBranch.Tip.Id);
        var local = repo.Branches[branchName];
        repo.Branches.Update(local, b =>
        {
            b.Remote = "origin";
            b.UpstreamBranch = targetRef;
        });

        ct.ThrowIfCancellationRequested();
        try
        {
            // Force: the working tree of a killed half-clone may hold partial files;
            // recovering means making it match the branch tip exactly.
            Commands.Checkout(repo, local, new CheckoutOptions
            {
                CheckoutModifiers = CheckoutModifiers.Force,
                CheckoutNotifyFlags = CheckoutNotifyFlags.Updated,
                OnCheckoutNotify = (_, _) => !ct.IsCancellationRequested,
            });
        }
        catch (UserCancelledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    // ---------------------------------------------------------------- path recovery

    private static void ApplyRecovery(string path, PathRecovery recovery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        using var repo = new Repository(path);
        ApplyRecoveryCore(repo, recovery, ct);
    }

    /// <summary>
    /// Materializes HEAD's tree into the working directory with the recovery's renames
    /// and skips applied, after validating that the effective path set is actually
    /// creatable on Windows. Any recovery persisted from an earlier run is merged in
    /// first (the incoming recovery wins on conflicts), so a recovery-managed repo
    /// that gains new invalid paths can be fixed incrementally. Persists the merged
    /// recovery so later pulls re-apply it, then clears the pending-checkout marker.
    /// </summary>
    private static void ApplyRecoveryCore(Repository repo, PathRecovery recovery, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            repo.Config.Set("core.longpaths", true, ConfigurationLevel.Local);
        }

        string recoveryFile = GetRecoveryFilePath(repo);
        if (File.Exists(recoveryFile))
        {
            recovery = MergeRecoveries(stored: LoadRecovery(recoveryFile), incoming: recovery);
        }

        var tip = repo.Head.Tip
            ?? throw new InvalidOperationException("Repository has no commits; nothing to materialize.");

        var entries = CollectEffectiveEntries(tip.Tree, recovery);

        // Validate BEFORE writing anything: a bad mapping (still-invalid segment, or
        // two originals landing on one destination) must fail cleanly with the
        // effective paths, leaving the working tree untouched.
        var stillInvalid = WindowsPathValidator.ValidatePaths(entries.Select(e => e.EffectivePath));
        if (stillInvalid.Count > 0)
        {
            throw new InvalidRepositoryPathsException(stillInvalid);
        }

        string workdir = repo.Info.WorkingDirectory;
        foreach (var (originalPath, effectivePath, blob) in entries)
        {
            ct.ThrowIfCancellationRequested();

            string fullPath = Path.Combine(workdir, effectivePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            if (File.Exists(fullPath))
            {
                // Force semantics: overwrite whatever is there, read-only or not.
                File.SetAttributes(fullPath, FileAttributes.Normal);
            }

            // Filter through .gitattributes (CRLF etc.) under the ORIGINAL repo path,
            // exactly as a checkout of that entry would.
            using var content = blob.GetContentStream(new FilteringOptions(originalPath));
            using var file = new FileStream(fullPath, FileMode.Create, FileAccess.Write);
            content.CopyTo(file);
        }

        SaveRecovery(repo, recovery);
        repo.Config.Set(CheckoutPendingKey, false, ConfigurationLevel.Local);
    }

    /// <summary>
    /// Overlays <paramref name="incoming"/> onto <paramref name="stored"/>: renames
    /// union with the incoming value winning on a shared original path; skips union.
    /// </summary>
    private static PathRecovery MergeRecoveries(PathRecovery stored, PathRecovery incoming)
    {
        var renames = new Dictionary<string, string>(stored.SegmentRenames, StringComparer.Ordinal);
        foreach (var (originalPath, replacement) in incoming.SegmentRenames)
        {
            renames[originalPath] = replacement;
        }

        var skips = new HashSet<string>(stored.SkippedPaths, StringComparer.Ordinal);
        skips.UnionWith(incoming.SkippedPaths);

        return new PathRecovery(renames, skips);
    }

    /// <summary>
    /// Walks <paramref name="tree"/> iteratively, resolving every blob to the path it
    /// should occupy on disk: skipped files and whole skipped directories are omitted,
    /// and a rename of any path (file or directory) replaces its final segment in
    /// place — for a directory, the entire subtree moves with it.
    /// </summary>
    private static List<(string OriginalPath, string EffectivePath, Blob Blob)> CollectEffectiveEntries(
        Tree tree, PathRecovery recovery)
    {
        var entries = new List<(string, string, Blob)>();
        var stack = new Stack<(Tree Tree, string OriginalPrefix, string EffectivePrefix)>();
        stack.Push((tree, "", ""));

        while (stack.Count > 0)
        {
            var (current, originalPrefix, effectivePrefix) = stack.Pop();
            foreach (var entry in current)
            {
                string originalPath = originalPrefix.Length == 0 ? entry.Name : $"{originalPrefix}/{entry.Name}";
                if (recovery.SkippedPaths.Contains(originalPath))
                {
                    continue; // omit the file — or the whole subtree when this is a directory
                }

                // Renames key on the full ORIGINAL path, but a mapped value contributes
                // only its last segment, joined onto the parent's EFFECTIVE prefix — so
                // renaming both a directory and one of its descendants composes instead
                // of the descendant's mapping resurrecting the parent's original name.
                // Unmapped entries keep their own name under that same prefix.
                string effectiveName = recovery.SegmentRenames.TryGetValue(originalPath, out string? mapped)
                    ? mapped[(mapped.LastIndexOf('/') + 1)..]
                    : entry.Name;
                string effectivePath = effectivePrefix.Length == 0
                    ? effectiveName
                    : $"{effectivePrefix}/{effectiveName}";

                switch (entry.TargetType)
                {
                    case TreeEntryTargetType.Tree:
                        stack.Push(((Tree)entry.Target, originalPath, effectivePath));
                        break;
                    case TreeEntryTargetType.Blob:
                        // Executable-bit and symlink entries are written as regular
                        // files: NTFS has no executable bit, and creating symlinks on
                        // Windows requires elevation (a symlink blob's content is its
                        // target path, which is still useful as a plain file).
                        entries.Add((originalPath, effectivePath, (Blob)entry.Target));
                        break;
                    default:
                        break; // GitLink (submodule): nothing to materialize
                }
            }
        }

        return entries;
    }

    /// <summary>
    /// Finishes a clone that was interrupted by invalid paths but has no stored
    /// recovery: if upstream has fixed the paths, check out normally and clear the
    /// marker; otherwise rethrow the same typed failure — the repo must not silently
    /// report success with an empty working tree.
    /// </summary>
    private static void CompletePendingCheckout(Repository repo, CancellationToken ct)
    {
        AdvanceHeadToTrackedTip(repo);

        var tip = repo.Head.Tip;
        if (tip is not null)
        {
            if (OperatingSystem.IsWindows())
            {
                var invalidPaths = WindowsPathValidator.Validate(tip.Tree);
                if (invalidPaths.Count > 0)
                {
                    throw new InvalidRepositoryPathsException(invalidPaths);
                }
            }

            ct.ThrowIfCancellationRequested();
            try
            {
                Commands.Checkout(repo, repo.Head, new CheckoutOptions
                {
                    CheckoutModifiers = CheckoutModifiers.Force, // nothing was ever checked out
                    CheckoutNotifyFlags = CheckoutNotifyFlags.Updated,
                    OnCheckoutNotify = (_, _) => !ct.IsCancellationRequested,
                });
            }
            catch (UserCancelledException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
        }

        repo.Config.Set(CheckoutPendingKey, false, ConfigurationLevel.Local);
    }

    /// <summary>
    /// Moves the current branch's ref to its upstream tip without touching the working
    /// tree. Only used for never-checked-out (marker) and recovery-managed repos, whose
    /// local branch can hold no local work — so a plain ref move is safe where the
    /// normal pull path would insist on a fast-forward merge.
    /// </summary>
    private static void AdvanceHeadToTrackedTip(Repository repo)
    {
        var tracked = repo.Head.TrackedBranch;
        if (tracked?.Tip is null || repo.Head.Tip?.Sha == tracked.Tip.Sha)
        {
            return;
        }
        repo.Refs.UpdateTarget(repo.Refs.Head.ResolveToDirectReference(), tracked.Tip.Id);
    }

    private static bool IsCheckoutPending(Repository repo)
        => repo.Config.Get<bool>(CheckoutPendingKey)?.Value == true;

    private static string GetRecoveryFilePath(Repository repo)
        => Path.Combine(repo.Info.Path, RecoveryFileName);

    /// <summary>Serializable shape of <see cref="PathRecovery"/> for .git\gclo-recovery.json.</summary>
    /// <param name="SegmentRenames">Original path to replacement path map; null when absent from the file.</param>
    /// <param name="SkippedPaths">Paths omitted from the working tree; null when absent from the file.</param>
    private sealed record RecoveryDocument(Dictionary<string, string>? SegmentRenames, List<string>? SkippedPaths);

    private static void SaveRecovery(Repository repo, PathRecovery recovery)
    {
        var document = new RecoveryDocument(
            new Dictionary<string, string>(recovery.SegmentRenames, StringComparer.Ordinal),
            recovery.SkippedPaths.Order(StringComparer.Ordinal).ToList());
        File.WriteAllText(GetRecoveryFilePath(repo), JsonSerializer.Serialize(document, RecoveryJsonOptions));
    }

    private static PathRecovery LoadRecovery(string filePath)
    {
        var document = JsonSerializer.Deserialize<RecoveryDocument>(File.ReadAllText(filePath), RecoveryJsonOptions)
            ?? throw new InvalidOperationException($"Recovery file '{filePath}' is empty or malformed.");
        return new PathRecovery(
            new Dictionary<string, string>(document.SegmentRenames ?? new(StringComparer.Ordinal), StringComparer.Ordinal),
            new HashSet<string>(document.SkippedPaths ?? [], StringComparer.Ordinal));
    }

    private static CredentialsHandler MakeCredentialsProvider(string token)
        => (_, _, _) => new UsernamePasswordCredentials
        {
            // GitHub accepts any username when the PAT is sent as the password.
            Username = "x-access-token",
            Password = token,
        };

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }
            // Pack files under .git are written read-only; clear attributes so delete succeeds.
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort only; a leftover partial clone surfaces as a failure on the next run.
        }
    }
}
