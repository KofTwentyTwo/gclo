using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace gclo.Engine;

/// <summary>
/// Git operations implemented with LibGit2Sharp.
/// </summary>
/// <remarks>
/// This is the native-libgit2 boundary, and it is excluded from the coverage metric
/// (like <see cref="OctokitGateway"/>, the network boundary) rather than measured
/// line-by-line. Its real behavior IS verified — the integration suites
/// (LibGit2GitClientTests, WindowsPathValidationTests, PathRecoveryTests, SyncOverloadTests)
/// drive it against on-disk fixture repositories and assert clone/pull/validation/recovery
/// outcomes. What cannot be counted is branch coverage that depends on things the offline
/// suite cannot deterministically produce: the transfer-progress callback (fires only on
/// pack/network transport, never on a local clone) and the
/// <see cref="UserCancelledException"/> → <see cref="OperationCanceledException"/> arms
/// (fire only when cancellation lands during a native libgit2 call). Measuring the class
/// would report those as gaps no offline test can close, so it is excluded wholesale and
/// its correctness is asserted behaviorally instead.
/// </remarks>
[ExcludeFromCodeCoverage(Justification =
    "Native libgit2 adapter; verified behaviorally by the integration suites. Transport- and "
    + "cancellation-timing branches are not deterministically reproducible offline. See remarks.")]
public sealed class LibGit2GitClient : IGitClient
{
    /// <summary>
    /// Local git config flag meaning "the objects are here, but the working tree was
    /// never completely checked out". Set the moment a clone's fetch completes and
    /// cleared only after checkout finishes, so it is durable evidence of an
    /// incomplete clone whatever interrupted it: Windows path validation (the repo is
    /// deliberately kept for recovery), a checkout error whose best-effort cleanup
    /// could not delete the directory, or the process dying mid-checkout. The next
    /// pull sees the flag and completes the checkout instead of comparing commit tips
    /// and calling an incomplete tree "up to date". Cleared by a normal checkout or by
    /// <see cref="ApplyRecoveryAsync"/>.
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

            // From here until checkout completes the directory is a valid repository
            // with no (or a partial) working tree. Record that durably first: if the
            // cleanup in the catch below cannot remove the directory, or the process
            // dies, the next run must not mistake this for a finished clone.
            repo.Config.Set(CheckoutPendingKey, true, ConfigurationLevel.Local);

            if (OperatingSystem.IsWindows())
            {
                // Lifts the 260-character path limit before anything touches the
                // working tree; libgit2 honors it for checkout.
                repo.Config.Set("core.longpaths", true, ConfigurationLevel.Local);
            }

            var tip = repo.Head.Tip;
            if (tip is null)
            {
                // Empty repository — nothing to check out, so nothing is pending.
                repo.Config.Set(CheckoutPendingKey, false, ConfigurationLevel.Local);
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                var invalidPaths = WindowsPathValidator.Validate(tip.Tree);
                if (invalidPaths.Count > 0)
                {
                    // Keep the fetched repo: all objects are already downloaded, so
                    // ApplyRecoveryAsync can materialize a sanitized working tree without
                    // touching the network. The pending marker (already set) tells
                    // FetchAndPull that this repository was never checked out.
                    throw new InvalidRepositoryPathsException(invalidPaths);
                }
            }

            ct.ThrowIfCancellationRequested();
            Commands.Checkout(repo, repo.Head, new CheckoutOptions
            {
                CheckoutModifiers = CheckoutModifiers.Force, // materialize the fresh working tree
                // The only checkout that lacked the hook: a canceled clone used to
                // write its whole tree before the token was ever observed (#31).
                CheckoutNotifyFlags = CheckoutNotifyFlags.Updated,
                OnCheckoutNotify = (_, _) => !ct.IsCancellationRequested,
            });

            // Only now is the clone complete. Config writes are atomic on disk, so an
            // observer sees either "pending" or "done", never a torn state.
            repo.Config.Set(CheckoutPendingKey, false, ConfigurationLevel.Local);
        }
        catch (Exception ex)
        {
            // Don't leave a half-cloned directory behind: the next run would treat a
            // partial checkout as "exists locally" and try to pull it. Invalid-path
            // failures are the deliberate exception — the fetched repo is kept (with
            // the pending marker set above) so recovery needs no re-download. If this
            // delete fails (open handle, antivirus, permissions) the marker is still
            // set, and the next pull completes the checkout instead of trusting the tree.
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
            // Drop tracking refs for branches origin deleted. Without this a renamed
            // default branch leaves refs/remotes/origin/<old> frozen at its last SHA,
            // and every later sync compares against it and says "up to date" (#31).
            Prune = true,
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

        // Repositories whose clone never finished its checkout — Windows-invalid
        // paths, a checkout error whose cleanup failed, a killed process — carry the
        // pending marker (and, once the user chose renames/skips, a persisted
        // recovery). Both take a dedicated path — their working tree is materialized
        // from the tip, never by a merge checkout that trusts the existing tree.
        // Every other repository takes the normal fetch + fast-forward pull below.
        string recoveryFile = GetRecoveryFilePath(repo);
        if (File.Exists(recoveryFile))
        {
            // A recovery-managed repo stays recovery-managed: re-materialize the new
            // tip through the stored mapping. Freshly-invalid paths the mapping does
            // not cover surface as a typed failure listing the effective paths.
            AdvanceHeadToTrackedTip(repo);
            RecoveryDocument stored = LoadRecoveryDocument(recoveryFile);
            if (stored.MaterializedTip is not null && stored.MaterializedTip == repo.Head.Tip?.Sha)
            {
                // The tree on disk already reflects this tip: nothing to rewrite. A
                // recovery-managed repo used to re-materialize every blob on every
                // sync, touching gigabytes and every mtime for zero change (#31).
                return;
            }
            ApplyRecoveryCore(repo, ToRecovery(stored), ct);
            return;
        }

        if (IsCheckoutPending(repo))
        {
            CompletePendingCheckout(repo, ct);
            return;
        }

        if (repo.Info.IsHeadDetached)
        {
            throw new InvalidOperationException(
                "HEAD is detached (a commit is checked out, not a branch); fetched, but nothing was merged. "
                + "Check out a branch in this repository to resume updates, or delete the folder to re-clone.");
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
            if (repo.Head.UpstreamBranchCanonicalName is null)
            {
                return; // no upstream configured: fetch is all we can do
            }

            // Upstream is configured but gone from origin (the fetch pruned it): the
            // default branch was renamed (master -> main) or deleted. Follow origin's
            // current default instead of reporting "up to date" forever (#31).
            tracked = RetargetToRemoteDefaultBranch(repo, remote, fetchOptions.CredentialsProvider, ct);
        }

        if (repo.Head.Tip?.Sha == tracked.Tip.Sha)
        {
            return; // already up to date
        }

        // Incoming commits can introduce Windows-invalid paths just like a clone can.
        // Only what the pull introduces is checked: the current tree was validated
        // when it was checked out, and re-walking a 100K-file tree for a one-file
        // commit was the dominant engine cost of a daily update run (#30).
        if (OperatingSystem.IsWindows())
        {
            // HEAD cannot be unborn here (handled above), so the current tip exists.
            var invalidIncoming = WindowsPathValidator.ValidateIncoming(repo, repo.Head.Tip!.Tree, tracked.Tip.Tree);
            if (invalidIncoming.Count > 0)
            {
                throw new InvalidRepositoryPathsException(invalidIncoming);
            }
        }

        // Fast-forward only: a mirror tool must never manufacture merge commits.
        // Diverged local history is the most common pull failure for this tool's
        // audience (someone committed in a synced clone), so it gets a message that
        // says what gclo refuses to do and what to do about it (#30).
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
        catch (NonFastForwardException ex)
        {
            throw new InvalidOperationException(
                $"'{repo.Head.FriendlyName}' has local commits that origin does not have; gclo only fast-forwards and never merges. "
                + "Push or reset the local commits, or delete the folder to re-clone.", ex);
        }
    }

    /// <summary>
    /// The current branch's upstream no longer exists on origin. Asks origin for its
    /// current default branch (the remote HEAD symref), checks it out as the new
    /// local branch with upstream wired, and removes the orphaned local branch — but
    /// only when every commit of the old branch is already contained in the new
    /// default, because a mirror tool never discards local work. Returns the new
    /// tracking branch so the normal pull continues from it.
    /// </summary>
    private static Branch RetargetToRemoteDefaultBranch(
        Repository repo, Remote remote, CredentialsHandler credentials, CancellationToken ct)
    {
        const string prefix = "refs/heads/";
        string oldName = repo.Head.FriendlyName;

        string? defaultRef = repo.Network.ListReferences(remote, credentials)
            .FirstOrDefault(r => r.CanonicalName == "HEAD")?.TargetIdentifier;
        if (defaultRef is null || !defaultRef.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{oldName}' no longer exists on origin, and origin has no default branch to follow. "
                + "Check the repository on GitHub, or delete the folder to re-clone.");
        }

        string newName = defaultRef[prefix.Length..];
        var remoteBranch = repo.Branches[$"{remote.Name}/{newName}"];
        if (remoteBranch?.Tip is null)
        {
            throw new InvalidOperationException(
                $"'{oldName}' no longer exists on origin; its default branch is now '{newName}', which was not fetched. "
                + "Delete the folder to re-clone.");
        }

        var oldTip = repo.Head.Tip;
        if (oldTip is not null && repo.ObjectDatabase.FindMergeBase(oldTip, remoteBranch.Tip)?.Sha != oldTip.Sha)
        {
            throw new InvalidOperationException(
                $"'{oldName}' no longer exists on origin (the default branch is now '{newName}'), but it has local commits "
                + $"that '{newName}' does not contain; gclo never discards local work. Push or reset them, or delete the folder to re-clone.");
        }

        var local = repo.Branches[newName] ?? repo.CreateBranch(newName, remoteBranch.Tip);
        repo.Branches.Update(local, b =>
        {
            b.Remote = remote.Name;
            b.UpstreamBranch = defaultRef;
        });

        ct.ThrowIfCancellationRequested();
        try
        {
            Commands.Checkout(repo, local, new CheckoutOptions
            {
                CheckoutNotifyFlags = CheckoutNotifyFlags.Updated,
                OnCheckoutNotify = (_, _) => !ct.IsCancellationRequested,
            });
        }
        catch (UserCancelledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        if (!string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            repo.Branches.Remove(oldName); // fully contained in the new default; nothing is lost
        }

        return repo.Head.TrackedBranch
            ?? throw new InvalidOperationException($"'{newName}' could not be set to track origin.");
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
        RecoveryDocument? previous = File.Exists(recoveryFile) ? LoadRecoveryDocument(recoveryFile) : null;
        if (previous is not null)
        {
            recovery = MergeRecoveries(stored: ToRecovery(previous), incoming: recovery);
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

        // A real checkout removes what left the tree; this materialization must too,
        // or a recovery-managed working tree accumulates every file upstream ever
        // deleted or renamed (#31). The previous apply's manifest says what it wrote.
        var effectiveNow = new HashSet<string>(entries.Select(e => e.EffectivePath), StringComparer.Ordinal);
        foreach (string stale in previous?.MaterializedPaths ?? [])
        {
            if (!effectiveNow.Contains(stale))
            {
                DeleteMaterializedFile(workdir, stale);
            }
        }

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

        SaveRecovery(repo, recovery, entries.Select(e => e.EffectivePath), tip.Sha);
        repo.Config.Set(CheckoutPendingKey, false, ConfigurationLevel.Local);
    }

    /// <summary>
    /// Removes one file a previous materialization wrote (a user-deleted file is
    /// fine) and prunes the directories it leaves empty, stopping at the working
    /// directory root.
    /// </summary>
    private static void DeleteMaterializedFile(string workdir, string effectivePath)
    {
        string fullPath = Path.Combine(workdir, effectivePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(fullPath))
        {
            File.SetAttributes(fullPath, FileAttributes.Normal);
            File.Delete(fullPath);
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workdir));
        string? directory = Path.GetDirectoryName(fullPath);
        while (directory is not null
            && !string.Equals(Path.TrimEndingDirectorySeparator(directory), root, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(directory)
            && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
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
    /// Finishes a clone whose checkout never completed and that has no stored
    /// recovery: moves to the upstream tip, re-validates its paths on Windows, and
    /// force-checks-out the whole tree (whatever partial files exist are overwritten
    /// to match). If the paths are still invalid, rethrows the same typed failure —
    /// the repo must not silently report success with an empty or partial tree.
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
    /// <param name="MaterializedPaths">Effective paths the last apply wrote, so the next one can delete what left the tree; null in files from before this field existed.</param>
    /// <param name="MaterializedTip">SHA of the commit the last apply materialized; null in older files.</param>
    private sealed record RecoveryDocument(
        Dictionary<string, string>? SegmentRenames,
        List<string>? SkippedPaths,
        List<string>? MaterializedPaths,
        string? MaterializedTip);

    private static void SaveRecovery(
        Repository repo, PathRecovery recovery, IEnumerable<string> materializedPaths, string tipSha)
    {
        var document = new RecoveryDocument(
            new Dictionary<string, string>(recovery.SegmentRenames, StringComparer.Ordinal),
            recovery.SkippedPaths.Order(StringComparer.Ordinal).ToList(),
            materializedPaths.Order(StringComparer.Ordinal).ToList(),
            tipSha);

        // Atomic replace: a crash mid-write must not leave a truncated document that
        // fails every later pull of this repository (#31).
        string path = GetRecoveryFilePath(repo);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(document, RecoveryJsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private static RecoveryDocument LoadRecoveryDocument(string filePath)
    {
        try
        {
            return JsonSerializer.Deserialize<RecoveryDocument>(File.ReadAllText(filePath), RecoveryJsonOptions)
                ?? throw new JsonException("the document is empty");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"The path-recovery file '{filePath}' is corrupt ({ex.Message}). "
                + "Delete it and use Resolve on this repository again to rebuild it.", ex);
        }
    }

    private static PathRecovery ToRecovery(RecoveryDocument document)
        => new(
            new Dictionary<string, string>(document.SegmentRenames ?? new(StringComparer.Ordinal), StringComparer.Ordinal),
            new HashSet<string>(document.SkippedPaths ?? [], StringComparer.Ordinal));

    /// <summary>
    /// GitHub accepts any username when the PAT is sent as the password. libgit2 asks
    /// again after every 401; handing it the same rejected token loops until it gives
    /// up with "too many redirects or authentication replays", once per repository.
    /// A correct token is never asked for a third time, so the third request means the
    /// token is bad and gets the same actionable message the API listers produce (#31).
    /// </summary>
    private static CredentialsHandler MakeCredentialsProvider(string token)
    {
        int requests = 0;
        return (_, _, _) =>
        {
            if (Interlocked.Increment(ref requests) > 2)
            {
                throw new InvalidOperationException(
                    "GitHub rejected the token for git over HTTPS (401). Check the PAT: it may have expired, "
                    + "been revoked, or lack access to this repository.");
            }
            return new UsernamePasswordCredentials
            {
                Username = "x-access-token",
                Password = token,
            };
        };
    }

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
