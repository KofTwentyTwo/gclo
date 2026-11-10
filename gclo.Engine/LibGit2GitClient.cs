/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

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

   /// <summary>
   /// Local git config flag meaning "gclo writes this repository's working tree
   /// itself, through a path recovery". It outlives the recovery file, so a
   /// repository whose file was deleted (as the message for a corrupt one advises)
   /// is still known to be recovery-managed and can be resolved again, instead of
   /// being taken for a clone someone checked out (#147). Set only once a recovery
   /// has been read or written successfully, never because a file merely exists.
   /// </summary>
   private const string RecoveryManagedKey = "gclo.recoverymanaged";

   /// <summary>File name (under .git) of the persisted <see cref="PathRecovery"/>.</summary>
   private const string RecoveryFileName = "gclo-recovery.json";

   private static readonly JsonSerializerOptions s_recoveryJsonOptions = new() { WriteIndented = true };

   /// <summary>No renames, no skips: every entry of a tree under its own path.</summary>
   private static readonly PathRecovery s_noRecovery = new(
       new Dictionary<string, string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

   /// <summary>
   /// Name of the remote that exists only for the duration of one fetch, when a
   /// folder's origin names the listed repository but spells its URL differently
   /// (see <see cref="FetchListedUrl"/>).
   /// </summary>
   private const string SyncRemoteName = "gclo-sync";

   /// <summary>
   /// The sync remote's own refspec. It maps to a namespace nothing is ever fetched
   /// into, so removing the remote (which deletes the tracking refs its refspecs
   /// cover) cannot touch origin's.
   /// </summary>
   private const string SyncRemoteRefSpec = "+refs/heads/*:refs/remotes/" + SyncRemoteName + "/*";

   /// <summary>
   /// Marks the sync remote as gclo's, so cleanup never removes a remote of the same
   /// name that the user created. It goes away with the remote.
   /// </summary>
   private const string SyncRemoteMarkerKey = "remote." + SyncRemoteName + ".gclo-temporary";

   /// <summary>
   /// Test seam: invoked right after a fetch completed, before the cancellation
   /// check that precedes any working-tree work. Local-transport fixtures fire no
   /// transfer callbacks, so this is how the offline suite exercises "canceled
   /// between fetch and checkout/merge" deterministically (#31). Null in production.
   /// </summary>
   internal Action? AfterFetchForTesting { get; set; }



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
   public Task FetchAndPullAsync(string url, string path, string token, CancellationToken cancellationToken)
       => Task.Factory.StartNew(
           () => FetchAndPull(url, path, token, cancellationToken),
           cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);



   /// <inheritdoc/>
   public Task ApplyRecoveryAsync(string path, PathRecovery recovery, CancellationToken cancellationToken)
       => Task.Factory.StartNew(
           () => ApplyRecovery(path, recovery, cancellationToken),
           cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);



   private void Clone(string url, string path, string token, Action<double>? onProgress, CancellationToken ct)
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
      options.FetchOptions.CredentialsProvider = MakeCredentialsProvider(token, url);
      int lastPercent = -1;
      options.FetchOptions.OnTransferProgress = tp =>
      {
         if(tp.TotalObjects > 0)
         {
            // libgit2 fires this per network read / indexed object — thousands of
            // times per clone. Forward only whole-percent changes so the UI
            // dispatcher is not flooded.
            int percent = (int)(100L * tp.ReceivedObjects / tp.TotalObjects);
            if(percent != lastPercent)
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
         AfterFetchForTesting?.Invoke();

         using var repo = new Repository(path);

         // From here until checkout completes the directory is a valid repository
         // with no (or a partial) working tree. Record that durably first: if the
         // cleanup in the catch below cannot remove the directory, or the process
         // dies, the next run must not mistake this for a finished clone.
         repo.Config.Set(CheckoutPendingKey, true, ConfigurationLevel.Local);

         if(OperatingSystem.IsWindows())
         {
            // Lifts the 260-character path limit before anything touches the
            // working tree; libgit2 honors it for checkout.
            repo.Config.Set("core.longpaths", true, ConfigurationLevel.Local);
         }

         Commit? tip = repo.Head.Tip;
         if(tip is null)
         {
            // Empty repository — nothing to check out, so nothing is pending.
            repo.Config.Set(CheckoutPendingKey, false, ConfigurationLevel.Local);
            return;
         }

         if(OperatingSystem.IsWindows())
         {
            IReadOnlyList<InvalidPathInfo> invalidPaths = WindowsPathValidator.Validate(tip.Tree);
            if(invalidPaths.Count > 0)
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
      catch(Exception ex)
      {
         // Don't leave a half-cloned directory behind: the next run would treat a
         // partial checkout as "exists locally" and try to pull it. Invalid-path
         // failures are the deliberate exception — the fetched repo is kept (with
         // the pending marker set above) so recovery needs no re-download. If this
         // delete fails (open handle, antivirus, permissions) the marker is still
         // set, and the next pull completes the checkout instead of trusting the tree.
         if(!existedBefore && ex is not InvalidRepositoryPathsException)
         {
            TryDeleteDirectory(path);
         }

         if(ex is UserCancelledException && ct.IsCancellationRequested)
         {
            throw new OperationCanceledException(ct);
         }
         throw;
      }
   }



   private void FetchAndPull(string url, string path, string token, CancellationToken ct)
   {
      using var repo = new Repository(path);

      if(OperatingSystem.IsWindows())
      {
         // Idempotent; also covers repositories that were cloned by other tools.
         repo.Config.Set("core.longpaths", true, ConfigurationLevel.Local);
      }

      Remote remote = OriginOfListedRepository(repo, url);

      var fetchOptions = new FetchOptions
      {
         CredentialsProvider = MakeCredentialsProvider(token, url),
         OnTransferProgress = _ => !ct.IsCancellationRequested,
         // Drop tracking refs for branches origin deleted. Without this a renamed
         // default branch leaves refs/remotes/origin/<old> frozen at its last SHA,
         // and every later sync compares against it and says "up to date" (#31).
         Prune = true,
      };

      try
      {
         FetchListedUrl(repo, remote, url, fetchOptions);
      }
      catch(UserCancelledException) when(ct.IsCancellationRequested)
      {
         throw new OperationCanceledException(ct);
      }
      AfterFetchForTesting?.Invoke();

      ct.ThrowIfCancellationRequested();

      // Repositories whose clone never finished its checkout — Windows-invalid
      // paths, a checkout error whose cleanup failed, a killed process — carry the
      // pending marker (and, once the user chose renames/skips, a persisted
      // recovery). Both take a dedicated path — their working tree is materialized
      // from the tip, never by a merge checkout that trusts the existing tree.
      // Every other repository takes the normal fetch + fast-forward pull below.
      if(File.Exists(GetRecoveryFilePath(repo)) || IsRecoveryManaged(repo)
          || (!IsCheckoutPending(repo) && WasWrittenWithoutAnIndex(repo) && HasPathsWindowsCannotCreate(repo)))
      {
         // A recovery-managed repo stays recovery-managed: the new tip is written
         // through the stored mapping, or nothing is touched when the tree on disk
         // already reflects it (a managed repo used to re-materialize every blob on
         // every sync, touching gigabytes and every mtime for zero change, #31).
         // Freshly-invalid paths the mapping does not cover surface as a typed
         // failure listing the effective paths.
         ApplyRecoveryCore(repo, incoming: null, ct);
         return;
      }

      if(IsCheckoutPending(repo))
      {
         CompletePendingCheckout(repo, ct);
         return;
      }

      if(repo.Info.IsHeadDetached)
      {
         throw new InvalidOperationException(
             "HEAD is detached (a commit is checked out, not a branch); fetched, but nothing was merged. "
             + "Check out a branch in this repository to resume updates, or delete the folder to re-clone.");
      }

      if(repo.Info.IsHeadUnborn)
      {
         // Empty clone — or a clone that was killed before its first checkout ran.
         // If the remote branch exists now, materialize it instead of silently
         // reporting an empty working tree as up to date forever.
         MaterializeUnbornHead(repo, ct);
         return;
      }

      Branch tracked = repo.Head.TrackedBranch;
      if(tracked?.Tip is null)
      {
         if(repo.Head.UpstreamBranchCanonicalName is null)
         {
            return; // no upstream configured: fetch is all we can do
         }

         // Upstream is configured but gone from origin (the fetch pruned it): the
         // default branch was renamed (master -> main) or deleted. Follow origin's
         // current default instead of reporting "up to date" forever (#31).
         // A provider of its own: the fetch already spent one of the two requests
         // the replay guard allows.
         tracked = RetargetToRemoteDefaultBranch(repo, remote, url, MakeCredentialsProvider(token, url), ct);
      }

      if(string.Equals(repo.Head.Tip?.Sha, tracked.Tip.Sha, StringComparison.Ordinal))
      {
         return; // already up to date
      }

      // Incoming commits can introduce Windows-invalid paths just like a clone can.
      // Only what the pull introduces is checked: the current tree was validated
      // when it was checked out, and re-walking a 100K-file tree for a one-file
      // commit was the dominant engine cost of a daily update run (#30).
      if(OperatingSystem.IsWindows())
      {
         // HEAD cannot be unborn here (handled above), so the current tip exists.
         IReadOnlyList<InvalidPathInfo> invalidIncoming = WindowsPathValidator.ValidateIncoming(repo, repo.Head.Tip!.Tree, tracked.Tip.Tree);
         if(invalidIncoming.Count > 0)
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
      catch(UserCancelledException) when(ct.IsCancellationRequested)
      {
         throw new OperationCanceledException(ct);
      }
      catch(NonFastForwardException ex)
      {
         throw new InvalidOperationException(
             $"'{repo.Head.FriendlyName}' has local commits that origin does not have; gclo only fast-forwards and never merges. "
             + "Push or reset the local commits, or delete the folder to re-clone.", ex);
      }
   }



   /// <summary>
   /// Fetches the listed <paramref name="url"/> into <paramref name="origin"/>'s own
   /// refspecs, so the tracking refs land exactly where a fetch of origin puts them
   /// while the remote that is contacted is the one the listing returned.
   /// </summary>
   /// <remarks>
   /// When origin's URL is the listed URL, which is every clone gclo made itself, this
   /// is a plain fetch of origin. Otherwise the origin names the same repository in
   /// another spelling: switched to SSH for pushing, plain HTTP, credentials typed
   /// into it. Fetching that origin would let the folder's configuration choose the
   /// transport. LibGit2Sharp cannot fetch a bare URL, so the listed URL is fetched
   /// through a remote that exists only for this call; origin itself is never
   /// rewritten. The user's own (global) git configuration still applies to that
   /// remote, as it does to everything git does for them (#139).
   /// </remarks>
   private static void FetchListedUrl(Repository repo, Remote origin, string url, FetchOptions options)
   {
      TryRemoveSyncRemote(repo); // left behind by a run that was killed mid-fetch

      var refSpecs = origin.FetchRefSpecs.Select(s => s.Specification).ToList();
      if(string.Equals(origin.Url, url, StringComparison.Ordinal))
      {
         RefuseSshCommandOfTheFolder(repo, origin.Url);
         Commands.Fetch(repo, origin.Name, refSpecs, options, logMessage: null);
         return;
      }

      if(repo.Network.Remotes[SyncRemoteName] is not null)
      {
         // Not marked as gclo's, or TryRemoveSyncRemote would have removed it.
         throw new InvalidOperationException(
             $"This repository has a remote of its own named '{SyncRemoteName}', a name gclo needs for a moment "
             + "to fetch the listed URL. Rename that remote, then sync again.");
      }

      Remote sync = repo.Network.Remotes.Add(SyncRemoteName, url, SyncRemoteRefSpec);
      repo.Config.Set(SyncRemoteMarkerKey, true, ConfigurationLevel.Local);
      try
      {
         RefuseSshCommandOfTheFolder(repo, sync.Url);
         // The fetch behaves like a fetch of origin in every respect but the URL.
         options.TagFetchMode = origin.TagFetchMode;
         Commands.Fetch(repo, SyncRemoteName, refSpecs, options, logMessage: null);
      }
      finally
      {
         TryRemoveSyncRemote(repo);
      }
   }



   /// <summary>
   /// Removes the remote <see cref="FetchListedUrl"/> adds, when it is there and carries
   /// gclo's marker; a remote of that name the user made themselves is left alone.
   /// Best effort: a failure here (another program holding .git/config open) must not
   /// replace the outcome of the fetch, and the next sync removes what is left.
   /// </summary>
   private static void TryRemoveSyncRemote(Repository repo)
   {
      try
      {
         if(repo.Network.Remotes[SyncRemoteName] is not null && repo.Config.Get<bool>(SyncRemoteMarkerKey)?.Value == true)
         {
            repo.Network.Remotes.Remove(SyncRemoteName);
         }
      }
      catch(LibGit2SharpException)
      {
         // See the summary: never mask the fetch's own result.
      }
   }



   /// <summary>
   /// A folder's own git configuration must not decide where a sync's fetch goes. Two
   /// settings could: <c>url.&lt;base&gt;.insteadOf</c>, which rewrites the URL of any
   /// remote, and (see <see cref="RefuseSshCommandOfTheFolder"/>) the program used for
   /// SSH. Both are refused when they come from the folder's own configuration; the
   /// user's global configuration is theirs and is honored (#139).
   /// </summary>
   private static void RefuseUrlRewritesOfTheFolder(Repository repo)
   {
      foreach(ConfigurationEntry<string> entry in repo.Config)
      {
         if(entry.Level >= ConfigurationLevel.Local
             && entry.Key.StartsWith("url.", StringComparison.OrdinalIgnoreCase)
             && entry.Key.EndsWith(".insteadof", StringComparison.OrdinalIgnoreCase))
         {
            throw new InvalidOperationException(
                "This folder's own git configuration rewrites remote URLs (a url.<base>.insteadOf entry in its .git/config), "
                + "so a fetch could go somewhere other than the listed repository. Remove that entry, then sync again.");
         }
      }
   }



   /// <summary>
   /// When the URL a fetch will really use is not HTTPS (the user's global git
   /// configuration rewrites it to SSH), the transport is an external SSH program.
   /// Which program must not be up to the folder: a <c>core.sshCommand</c> in its own
   /// configuration is refused (#139).
   /// </summary>
   private static void RefuseSshCommandOfTheFolder(Repository repo, string effectiveUrl)
   {
      if(RemoteUrl.IsHttps(effectiveUrl))
      {
         return;
      }

      foreach(ConfigurationEntry<string> entry in repo.Config)
      {
         if(entry.Level >= ConfigurationLevel.Local && string.Equals(entry.Key, "core.sshcommand", StringComparison.OrdinalIgnoreCase))
         {
            throw new InvalidOperationException(
                "This folder's own git configuration sets core.sshCommand, and this fetch would go over SSH. "
                + "gclo does not run a program chosen by the folder it is syncing. Remove that entry from its .git/config, then sync again.");
         }
      }
   }



   /// <summary>
   /// The folder's <c>origin</c>, once it is known to name the repository listed at
   /// <paramref name="url"/>. A folder with the right name is not proof of the right
   /// repository: it may be a fork, a same-named repository of another owner or host,
   /// or something another tool put there. Pulling it would report the listed
   /// repository as updated when it was not, so it fails here, before any network
   /// request (#139).
   /// </summary>
   private static Remote OriginOfListedRepository(Repository repo, string url)
   {
      RefuseUrlRewritesOfTheFolder(repo);

      Remote remote = repo.Network.Remotes["origin"]
          ?? throw new InvalidOperationException("Repository has no 'origin' remote.");

      if(!RemoteUrl.IsSameRepository(url, remote.Url))
      {
         string folder = Path.TrimEndingDirectorySeparator(repo.Info.WorkingDirectory ?? repo.Info.Path);
         throw new InvalidOperationException(
             $"This folder is a clone of a different repository: its origin is '{RemoteUrl.Redact(remote.Url)}', not '{url}'. "
             + "If the repository was renamed or moved to another owner, point the folder at it with "
             + $"'git -C \"{folder}\" remote set-url origin {url}'. Otherwise move or delete the folder. Then sync again.");
      }

      return remote;
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
       Repository repo, Remote remote, string url, CredentialsHandler credentials, CancellationToken ct)
   {
      const string Prefix = "refs/heads/";
      string oldName = repo.Head.FriendlyName;

      string? defaultRef = repo.Network.ListReferences(url, credentials)
          .FirstOrDefault(r => string.Equals(r.CanonicalName, "HEAD", StringComparison.Ordinal))?.TargetIdentifier;
      if(defaultRef is null || !defaultRef.StartsWith(Prefix, StringComparison.Ordinal))
      {
         throw new InvalidOperationException(
             $"'{oldName}' no longer exists on origin, and origin has no default branch to follow. "
             + "Check the repository on GitHub, or delete the folder to re-clone.");
      }

      string newName = defaultRef[Prefix.Length..];
      Branch remoteBranch = repo.Branches[$"{remote.Name}/{newName}"];
      if(remoteBranch?.Tip is null)
      {
         throw new InvalidOperationException(
             $"'{oldName}' no longer exists on origin; its default branch is now '{newName}', which was not fetched. "
             + "Delete the folder to re-clone.");
      }

      Commit? oldTip = repo.Head.Tip;
      if(oldTip is not null && !string.Equals(repo.ObjectDatabase.FindMergeBase(oldTip, remoteBranch.Tip)?.Sha, oldTip.Sha, StringComparison.Ordinal))
      {
         throw new InvalidOperationException(
             $"'{oldName}' no longer exists on origin (the default branch is now '{newName}'), but it has local commits "
             + $"that '{newName}' does not contain; gclo never discards local work. Push or reset them, or delete the folder to re-clone.");
      }

      Branch local = repo.Branches[newName] ?? repo.CreateBranch(newName, remoteBranch.Tip);
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
      catch(UserCancelledException) when(ct.IsCancellationRequested)
      {
         throw new OperationCanceledException(ct);
      }

      if(!string.Equals(oldName, newName, StringComparison.Ordinal))
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
      const string Prefix = "refs/heads/";
      string targetRef = repo.Refs.Head.TargetIdentifier;
      if(!targetRef.StartsWith(Prefix, StringComparison.Ordinal))
      {
         return;
      }

      string branchName = targetRef[Prefix.Length..];
      Branch remoteBranch = repo.Branches["origin/" + branchName];
      if(remoteBranch?.Tip is null)
      {
         return; // the remote is still empty; nothing to pull
      }

      ///////////////////////////////////////////////////////////////////////////////
      // No commit on the branch does not mean nothing was checked out: a branch   //
      // started with "checkout --orphan" keeps the index and the working tree of  //
      // the branch it was started from. Checking out over that, with force, would //
      // replace someone's files.                                                  //
      ///////////////////////////////////////////////////////////////////////////////
      if(repo.Index.Count > 0)
      {
         throw new InvalidOperationException(
             $"'{branchName}' has no commits here yet, but files are staged in this folder (a branch started with "
             + "'git checkout --orphan'?). gclo does not check out over them; fetched, but nothing was checked out. "
             + "Commit or remove them, or check out another branch.");
      }

      if(OperatingSystem.IsWindows())
      {
         IReadOnlyList<InvalidPathInfo> invalidPaths = WindowsPathValidator.Validate(remoteBranch.Tip.Tree);
         if(invalidPaths.Count > 0)
         {
            throw new InvalidRepositoryPathsException(invalidPaths);
         }
      }

      repo.Refs.Add(targetRef, remoteBranch.Tip.Id);
      Branch local = repo.Branches[branchName];
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
      catch(UserCancelledException) when(ct.IsCancellationRequested)
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
   /// Materializes a commit's tree into the working directory with renames and skips
   /// applied, after validating that the effective path set is actually creatable on
   /// Windows. <paramref name="incoming"/> is what the user just chose (it wins over
   /// stored choices path by path), or null when a pull re-applies what is stored.
   /// </summary>
   /// <remarks>
   /// <para>
   /// This writes a working tree by hand, with force. It is therefore only ever done
   /// to a tree that is gclo's to write (#147):
   /// </para>
   /// <list type="bullet">
   /// <item>A repository whose clone was fetched but never checked out (the pending
   /// marker), or that is empty: nothing was ever written there.</item>
   /// <item>A recovery-managed repository: an earlier apply wrote its tree, and the
   /// documentation says to treat it as a read-only mirror.</item>
   /// </list>
   /// <para>
   /// A clone that was checked out normally is refused, whatever its state. Proving
   /// that such a clone holds nothing of the user's is not possible in general
   /// (untracked files, files git was told to ignore changes in, an apply interrupted
   /// half-way and edited since), and a wrong guess destroys work. The way to get such
   /// a repository with renamed paths is a fresh clone.
   /// </para>
   /// <para>
   /// Commits are protected in every case: the branch only moves where
   /// <see cref="TargetOf"/> allows. And an apply that is interrupted is completed by
   /// the next pull: before the first destructive step the recovery file is saved
   /// without a materialized tip, listing everything that was and will be on disk.
   /// </para>
   /// </remarks>
   private static void ApplyRecoveryCore(Repository repo, PathRecovery? incoming, CancellationToken ct)
   {
      string workdir = WorkingDirectoryOf(repo);
      string recoveryFile = GetRecoveryFilePath(repo);
      RecoveryDocument? stored = File.Exists(recoveryFile) ? LoadRecoveryDocument(recoveryFile, workdir) : null;
      // "Managed" by gclo's own word (its file, its flag), or only by what the
      // repository looks like; the second is trusted less (see below).
      bool managedByRecord = stored is not null || IsRecoveryManaged(repo);
      bool managed = managedByRecord || WasWrittenWithoutAnIndex(repo);

      ///////////////////////////////////////////////////////////////////////////////
      // An empty repository counts as never checked out only while git has staged //
      // nothing in it either: a branch started with "checkout --orphan" has no     //
      // commit yet, and a working tree full of someone's files.                    //
      ///////////////////////////////////////////////////////////////////////////////
      bool emptyAndUntouched = repo.Info.IsHeadUnborn && repo.Index.Count == 0;
      if(!managed && !IsCheckoutPending(repo) && !emptyAndUntouched)
      {
         throw new InvalidOperationException(
             "This repository is checked out normally. Resolving its paths would replace the working tree with one that gclo "
             + "writes itself, which could overwrite work in it, so gclo does not do that to a clone that was checked out. "
             + $"To get it with renamed paths, move or delete the folder '{Path.TrimEndingDirectorySeparator(workdir)}' and sync again"
             + (OperatingSystem.IsWindows() ? ", or use Clone in WSL in the desktop app." : "."));
      }

      // Only now: a refused apply leaves the repository's configuration as it was too.
      if(OperatingSystem.IsWindows())
      {
         repo.Config.Set("core.longpaths", true, ConfigurationLevel.Local);
      }

      PathRecovery recovery = MergeRecoveries(stored is null ? s_noRecovery : ToRecovery(stored), incoming ?? s_noRecovery);
      Commit tip = TargetOf(repo);
      if(incoming is null && IsAlreadyOnDisk(repo, stored, tip))
      {
         // (The file was read without complaint, so the repository is known to be
         // managed from here on even if the file is lost.)
         repo.Config.Set(RecoveryManagedKey, true, ConfigurationLevel.Local);
         return;
      }

      List<(string OriginalPath, string EffectivePath, Blob Blob)> entries = CollectEffectiveEntries(tip.Tree, recovery);

      // Validate BEFORE writing anything: a bad mapping (still-invalid segment, or
      // two originals landing on one destination) must fail cleanly with the
      // effective paths, leaving the working tree and the branch untouched.
      IReadOnlyList<InvalidPathInfo> stillInvalid = WindowsPathValidator.ValidatePaths(entries.Select(e => e.EffectivePath));
      if(stillInvalid.Count > 0)
      {
         throw new InvalidRepositoryPathsException(stillInvalid);
      }

      HashSet<string>? ours = stored?.MaterializedPaths is null ? null : new HashSet<string>(stored.MaterializedPaths, StringComparer.Ordinal);
      var effectiveNow = new HashSet<string>(entries.Select(e => e.EffectivePath), StringComparer.Ordinal);
      RefuseWhatCannotBeWritten(repo, workdir, entries, ours, effectiveNow, replaceOnlyWhatTheCommitHas: !managedByRecord);

      /////////////////////////////////////////////////////////////////////////////
      // The point of no return. Record first that this tree is being rewritten,   //
      // and everything that was and will be on disk by gclo's doing; then move the //
      // branch, describe the commit in the index (as after any checkout: without   //
      // it git reports every file as deleted, and attribute lookups can already    //
      // see the new tree), remove what left the tree, and write the files.         //
      /////////////////////////////////////////////////////////////////////////////
      SaveRecovery(repo, recovery, (ours ?? []).Union(effectiveNow, StringComparer.Ordinal), materializedTip: null);
      repo.Config.Set(RecoveryManagedKey, true, ConfigurationLevel.Local);
      MoveHeadTo(repo, tip);
      repo.Index.Replace(tip.Tree);
      repo.Index.Write();

      // A real checkout removes what left the tree; this materialization must too,
      // or the working tree accumulates every file upstream ever deleted or renamed
      // (#31).
      foreach(string stale in ours ?? [])
      {
         if(!effectiveNow.Contains(stale))
         {
            WorkingTree.Delete(workdir, stale);
         }
      }

      // Parent folders are shared by many files; each is checked for a link once.
      // Attribute files go first: they decide how the files after them are written
      // (line endings), and a stale one on disk would otherwise outrank the index.
      var verifiedFolders = new HashSet<string>(StringComparer.Ordinal);
      foreach((string? originalPath, string? effectivePath, Blob? blob) in entries.OrderBy(e => IsAttributesFile(e.OriginalPath) ? 0 : 1))
      {
         ct.ThrowIfCancellationRequested();

         // Filter through .gitattributes (CRLF etc.) under the ORIGINAL repo path,
         // exactly as a checkout of that entry would. Force semantics: whatever is
         // at the destination is replaced, read-only or not (see WorkingTree).
         using Stream content = blob.GetContentStream(new FilteringOptions(originalPath));
         using FileStream file = WorkingTree.Create(workdir, effectivePath, verifiedFolders);
         content.CopyTo(file);
      }

      SaveRecovery(repo, recovery, effectiveNow, tip.Sha);
      repo.Config.Set(CheckoutPendingKey, false, ConfigurationLevel.Local);
   }



   /// <summary>
   /// The repository's own word that gclo wrote its working tree. Read from the
   /// repository's configuration only: a value set for the user or the machine says
   /// nothing about this folder.
   /// </summary>
   private static bool IsRecoveryManaged(Repository repo)
       => repo.Config.Get<bool>(RecoveryManagedKey, ConfigurationLevel.Local)?.Value == true;



   /// <summary>
   /// True for a repository that gclo cloned (it carries gclo's checkout marker,
   /// whatever its value), has commits, and has no index: git never checked it out,
   /// so whatever tree is there was written by gclo. That is how a repository looks
   /// that the released build resolved (it wrote files and no index), also after its
   /// recovery file was lost. A clone made by git, with or without a checkout, has
   /// no such marker and is not taken for one. The inference is still only an
   /// inference (an index can be deleted by hand), so a tree recognised this way is
   /// rewritten only where its files are what the current commit has
   /// (<see cref="RefuseWhatCannotBeWritten"/>).
   /// </summary>
   private static bool WasWrittenWithoutAnIndex(Repository repo)
       => !repo.Info.IsHeadUnborn
           && repo.Config.Get<bool>(CheckoutPendingKey, ConfigurationLevel.Local) is not null
           && !File.Exists(Path.Combine(repo.Info.Path, "index"));



   /// <summary>
   /// True when the current commit holds a path that cannot be created here. Only
   /// then can a tree without an index be one that path recovery wrote: a repository
   /// with nothing to rename is an ordinary clone whatever became of its index, and
   /// takes the ordinary pull, which leaves local changes alone and marks nothing.
   /// </summary>
   private static bool HasPathsWindowsCannotCreate(Repository repo)
       => OperatingSystem.IsWindows() && WindowsPathValidator.Validate(repo.Head.Tip.Tree).Count > 0;



   /// <summary>
   /// True when a pull has nothing to write: the branch already is at the commit to
   /// write, and what gclo last wrote is that commit or one it builds on. In the
   /// second case the commits on top were made here and origin brought nothing;
   /// rewriting the tree to what was committed would throw away everything else in it.
   /// </summary>
   private static bool IsAlreadyOnDisk(Repository repo, RecoveryDocument? stored, Commit tip)
   {
      if(stored?.MaterializedTip is not { } written
          || !string.Equals(repo.Head.Tip?.Sha, tip.Sha, StringComparison.Ordinal))
      {
         return false; // an apply is unfinished, or the branch still has to move
      }

      if(string.Equals(written, tip.Sha, StringComparison.Ordinal))
      {
         return true;
      }

      ///////////////////////////////////////////////////////////////////////////////
      // The branch is ahead of what was written. That is "commits made here" only  //
      // when origin has nothing beyond what was written. If origin does, the       //
      // branch was moved to origin's commit and the tree was never written for it: //
      // the released build moved the branch first, so a pull of its that failed    //
      // half-way (a new invalid path, a cancel, a locked file) leaves exactly that. //
      ///////////////////////////////////////////////////////////////////////////////
      Commit? writtenCommit = ObjectId.TryParse(written, out ObjectId writtenId) ? repo.Lookup<Commit>(writtenId) : null;
      if(writtenCommit is null)
      {
         return false;
      }

      Commit? upstream = repo.Info.IsHeadDetached ? null : repo.Head.TrackedBranch?.Tip;
      bool originHasNothingNew = upstream is null
          || string.Equals(repo.ObjectDatabase.FindMergeBase(upstream, writtenCommit)?.Sha, upstream.Sha, StringComparison.Ordinal);
      return originHasNothingNew
          && string.Equals(repo.ObjectDatabase.FindMergeBase(writtenCommit, tip)?.Sha, written, StringComparison.Ordinal);
   }



   private static bool IsAttributesFile(string repoPath)
       => repoPath.EndsWith(".gitattributes", StringComparison.Ordinal)
           && (repoPath.Length == ".gitattributes".Length || repoPath[^(".gitattributes".Length + 1)] == '/');



   /// <summary>
   /// The commit an apply writes. gclo moves a branch only to what origin has, and only
   /// when that drops no commit (#147):
   /// <list type="bullet">
   /// <item>An empty repository (unborn HEAD): origin's tip for that branch, its first
   /// checkout.</item>
   /// <item>Origin has nothing the current commit lacks (no upstream, a detached HEAD,
   /// the same commit, or commits made here on top of origin's): the current
   /// commit.</item>
   /// <item>The current commit is contained in origin's tip: origin's tip.</item>
   /// <item>The histories diverged: origin's tip only when the current commit is one
   /// origin's branch itself once pointed at (<see cref="WasOnceTheTipOf"/>). Then
   /// nobody committed here and origin rewrote its history. Otherwise a refusal.</item>
   /// </list>
   /// </summary>
   private static Commit TargetOf(Repository repo)
   {
      Commit? head = repo.Head.Tip;
      if(head is null)
      {
         const string Prefix = "refs/heads/";
         string targetRef = repo.Refs.Head.TargetIdentifier;
         Commit? first = targetRef.StartsWith(Prefix, StringComparison.Ordinal)
             ? repo.Branches["origin/" + targetRef[Prefix.Length..]]?.Tip
             : null;
         return first ?? throw new InvalidOperationException(
             "This repository has no commits, and neither has its branch on origin; there is nothing to materialize.");
      }

      Branch? tracked = repo.Info.IsHeadDetached ? null : repo.Head.TrackedBranch;
      Commit? upstream = tracked?.Tip;
      if(upstream is null)
      {
         return head;
      }

      string? mergeBase = repo.ObjectDatabase.FindMergeBase(head, upstream)?.Sha;
      if(string.Equals(mergeBase, upstream.Sha, StringComparison.Ordinal))
      {
         return head;
      }

      if(string.Equals(mergeBase, head.Sha, StringComparison.Ordinal) || WasOnceTheTipOf(repo, tracked!, head))
      {
         return upstream;
      }

      throw new InvalidOperationException(tracked!.IsRemote
          ? $"'{repo.Head.FriendlyName}' and its branch on origin have diverged, and nothing here shows that the commits "
            + "in this folder came from origin: either commits were made here, or origin's history was rewritten and the "
            + "record of what was fetched is gone. gclo only fast-forwards and never merges. "
            + "Push or reset commits made here, or delete the folder to re-clone."
          : $"'{repo.Head.FriendlyName}' and the local branch it tracks ('{tracked.FriendlyName}') have diverged. "
            + "gclo only fast-forwards and never merges. Merge or reset by hand, or check out a branch that tracks origin.");
   }



   /// <summary>
   /// True when origin's branch itself pointed at <paramref name="commit"/> at some
   /// time, going by the reflog of the remote-tracking branch <paramref name="tracked"/>:
   /// every clone, fetch, and pull that moves the branch adds an entry saying where
   /// it went, and only those entries count. Other things write there too (a push
   /// records the commit that was made here, a hand-made update records anything),
   /// and they do not. Where the branch was before the oldest entry that is left
   /// (older ones expire) counts as well. A branch that tracks another local branch
   /// has no such record of origin, and nothing is taken from its reflog.
   /// </summary>
   private static bool WasOnceTheTipOf(Repository repo, Branch tracked, Commit commit)
   {
      if(!tracked.IsRemote)
      {
         return false;
      }

      ReflogEntry? oldest = null;
      foreach(ReflogEntry entry in repo.Refs.Log(tracked.CanonicalName)) // newest first
      {
         if(entry.To == commit.Id && IsWrittenByAFetch(entry))
         {
            return true;
         }
         oldest = entry;
      }
      return oldest is not null && oldest.From == commit.Id;
   }



   /// <summary>True for a reflog entry whose message says a clone, a fetch, or a pull wrote it.</summary>
   private static bool IsWrittenByAFetch(ReflogEntry entry)
   {
      string message = entry.Message ?? "";
      return message.StartsWith("fetch", StringComparison.Ordinal)
          || message.StartsWith("pull", StringComparison.Ordinal)
          || message.StartsWith("clone", StringComparison.Ordinal);
   }



   /// <summary>Points the current branch at <paramref name="tip"/>, creating it (with its upstream) for an empty repository.</summary>
   private static void MoveHeadTo(Repository repo, Commit tip)
   {
      if(repo.Info.IsHeadUnborn)
      {
         string targetRef = repo.Refs.Head.TargetIdentifier;
         repo.Refs.Add(targetRef, tip.Id);
         Branch local = repo.Branches[targetRef["refs/heads/".Length..]];
         repo.Branches.Update(local, b =>
         {
            b.Remote = "origin";
            b.UpstreamBranch = targetRef;
         });
         return;
      }

      if(!string.Equals(repo.Head.Tip.Sha, tip.Sha, StringComparison.Ordinal))
      {
         repo.Refs.UpdateTarget(repo.Refs.Head.ResolveToDirectReference(), tip.Id);
      }
   }



   /// <summary>
   /// Finds, before anything has changed, what it can of what would make the apply
   /// fail half-way or write over something that is not gclo's: another git process at
   /// work, a destination that is not a usable path in the working tree, a link on the
   /// way to it or to a file that is to be removed, a file sitting where a folder is
   /// needed, a folder holding someone's files where a file is needed, and, where
   /// gclo has no record of having written the tree, a file at a destination that is
   /// not what the current commit has there. Failing here leaves the branch, the
   /// index and every file as they were (#147). What only shows while writing (a file
   /// held open) still fails later; the next pull then completes the apply.
   /// </summary>
   /// <remarks>
   /// <c>replaceOnlyWhatTheCommitHas</c> is true when nothing records that gclo wrote
   /// this tree (a clone that was never checked out, an empty one, a tree recognised
   /// only by its shape). A file that is already at a destination is then replaced
   /// only when it is, byte for byte, what the current commit has for that path: an
   /// unfinished checkout's file, not something a person put there or changed.
   /// </remarks>
   private static void RefuseWhatCannotBeWritten(
       Repository repo,
       string workdir,
       List<(string OriginalPath, string EffectivePath, Blob Blob)> entries,
       HashSet<string>? ours,
       HashSet<string> effectiveNow,
       bool replaceOnlyWhatTheCommitHas)
   {
      if(File.Exists(Path.Combine(repo.Info.Path, "index.lock")))
      {
         throw new InvalidOperationException(
             "Another git process is working in this repository (its index is locked). Try again when it has finished; "
             + $"if no git is running, one that crashed left '{Path.Combine(repo.Info.Path, "index.lock")}' behind, and deleting that file clears it.");
      }

      Tree? current = replaceOnlyWhatTheCommitHas ? repo.Head.Tip?.Tree : null;

      // The file system decides whether two names are one file; on Windows it does
      // not tell 'Docs' from 'docs', so neither does the question "is this ours".
      var oursAsOnDisk = new HashSet<string>(
          ours ?? [], OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
      var verifiedFolders = new HashSet<string>(StringComparer.Ordinal);
      var checkedFolders = new HashSet<string>(StringComparer.Ordinal);
      foreach((string? originalPath, string? effectivePath, _) in entries)
      {
         WorkingTree.Verify(workdir, effectivePath, verifiedFolders);

         string full = Path.Combine(workdir, effectivePath.Replace('/', Path.DirectorySeparatorChar));
         if(replaceOnlyWhatTheCommitHas && File.Exists(full) && !IsWhatTheCommitHas(full, originalPath, current))
         {
            throw new InvalidOperationException(
                $"'{effectivePath}' is already in this folder, and it is not a file gclo has a record of writing "
                + "or one the current commit has in that form, so it is not gclo's to replace. Move or delete it, then try again; "
                + "if that goes for many files, move or delete the whole folder and sync again.");
         }

         string? foreign = WorkingTree.FilesUnder(workdir, effectivePath).FirstOrDefault(file => !oursAsOnDisk.Contains(file));
         if(foreign is not null)
         {
            throw new InvalidOperationException(
                $"'{effectivePath}' is a folder with files in it ('{foreign}' is one), where the repository has a file. "
                + "Move or delete the folder, then try again.");
         }

         for(int slash = effectivePath.IndexOf('/', StringComparison.Ordinal); slash > 0; slash = effectivePath.IndexOf('/', slash + 1))
         {
            string folder = effectivePath[..slash];
            if(checkedFolders.Add(folder)
                && !oursAsOnDisk.Contains(folder)
                && File.Exists(Path.Combine(workdir, folder.Replace('/', Path.DirectorySeparatorChar))))
            {
               throw new InvalidOperationException(
                   $"'{folder}' is a file in this folder, and the repository has a folder of that name. "
                   + "Move or delete the file, then try again.");
            }
         }
      }

      // What left the tree is removed through real folders only, like what is written.
      foreach(string stale in ours ?? [])
      {
         if(!effectiveNow.Contains(stale))
         {
            WorkingTree.Verify(workdir, stale, verifiedFolders);
         }
      }
   }



   /// <summary>
   /// True when the file at <paramref name="full"/> holds exactly what a checkout of
   /// <paramref name="commitTree"/> writes for <paramref name="originalPath"/>
   /// (line-ending conversion included). False when the commit has no such file.
   /// </summary>
   private static bool IsWhatTheCommitHas(string full, string originalPath, Tree? commitTree)
   {
      if(commitTree?[originalPath]?.Target is not Blob blob)
      {
         return false;
      }

      using Stream expected = blob.GetContentStream(new FilteringOptions(originalPath));
      using var actual = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
      byte[] expectedBuffer = new byte[81920];
      byte[] actualBuffer = new byte[81920];
      while(true)
      {
         int expectedRead = expected.ReadAtLeast(expectedBuffer, expectedBuffer.Length, throwOnEndOfStream: false);
         int actualRead = actual.ReadAtLeast(actualBuffer, actualBuffer.Length, throwOnEndOfStream: false);
         if(expectedRead != actualRead || !expectedBuffer.AsSpan(0, expectedRead).SequenceEqual(actualBuffer.AsSpan(0, actualRead)))
         {
            return false;
         }
         if(expectedRead == 0)
         {
            return true;
         }
      }
   }



   /// <summary>
   /// Overlays <paramref name="incoming"/> onto <paramref name="stored"/>. The newer
   /// choice for a path wins whatever kind it is: an incoming rename replaces a stored
   /// rename or a stored skip of that path, and an incoming skip replaces a stored
   /// rename. Everything else is kept from both.
   /// </summary>
   private static PathRecovery MergeRecoveries(PathRecovery stored, PathRecovery incoming)
   {
      var renames = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach((string? originalPath, string? replacement) in stored.SegmentRenames)
      {
         if(!incoming.SkippedPaths.Contains(originalPath))
         {
            renames[originalPath] = replacement;
         }
      }
      foreach((string? originalPath, string? replacement) in incoming.SegmentRenames)
      {
         renames[originalPath] = replacement;
      }

      var skips = new HashSet<string>(
          stored.SkippedPaths.Where(path => !incoming.SegmentRenames.ContainsKey(path)), StringComparer.Ordinal);
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

      while(stack.Count > 0)
      {
         (Tree? current, string? originalPrefix, string? effectivePrefix) = stack.Pop();
         foreach(TreeEntry? entry in current)
         {
            string originalPath = originalPrefix.Length == 0 ? entry.Name : $"{originalPrefix}/{entry.Name}";
            if(recovery.SkippedPaths.Contains(originalPath))
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

            switch(entry.TargetType)
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

      Commit? tip = repo.Head.Tip;
      if(tip is not null)
      {
         if(OperatingSystem.IsWindows())
         {
            IReadOnlyList<InvalidPathInfo> invalidPaths = WindowsPathValidator.Validate(tip.Tree);
            if(invalidPaths.Count > 0)
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
         catch(UserCancelledException) when(ct.IsCancellationRequested)
         {
            throw new OperationCanceledException(ct);
         }
      }

      repo.Config.Set(CheckoutPendingKey, false, ConfigurationLevel.Local);
   }



   /// <summary>
   /// Moves the current branch to where origin's is, without touching the working
   /// tree, for a repository whose clone was never checked out. By the rule every
   /// branch move here follows (<see cref="TargetOf"/>): a commit somebody made in
   /// it by hand is not dropped.
   /// </summary>
   private static void AdvanceHeadToTrackedTip(Repository repo)
   {
      if(repo.Info.IsHeadUnborn)
      {
         return;
      }

      Commit target = TargetOf(repo);
      if(!string.Equals(repo.Head.Tip.Sha, target.Sha, StringComparison.Ordinal))
      {
         repo.Refs.UpdateTarget(repo.Refs.Head.ResolveToDirectReference(), target.Id);
      }
   }



   /// <summary>The never-checked-out marker, from the repository's own configuration only.</summary>
   private static bool IsCheckoutPending(Repository repo)
       => repo.Config.Get<bool>(CheckoutPendingKey, ConfigurationLevel.Local)?.Value == true;



   private static string GetRecoveryFilePath(Repository repo)
       => Path.Combine(repo.Info.Path, RecoveryFileName);



   /// <summary>Serializable shape of <see cref="PathRecovery"/> for .git\gclo-recovery.json.</summary>
   /// <param name="SegmentRenames">Original path to replacement path map; null when absent from the file.</param>
   /// <param name="SkippedPaths">Paths omitted from the working tree; null when absent from the file.</param>
   /// <param name="MaterializedPaths">Effective paths the last apply wrote, so the next one can delete what left the tree; null in files from before this field existed.</param>
   /// <param name="MaterializedTip">SHA of the commit the last apply materialized; null in older files, and while an apply is under way (so a tree left unfinished is never taken for done).</param>
   private sealed record RecoveryDocument(
       Dictionary<string, string>? SegmentRenames,
       List<string>? SkippedPaths,
       List<string>? MaterializedPaths,
       string? MaterializedTip);



   private static void SaveRecovery(
       Repository repo, PathRecovery recovery, IEnumerable<string> materializedPaths, string? materializedTip)
   {
      var document = new RecoveryDocument(
          new Dictionary<string, string>(recovery.SegmentRenames, StringComparer.Ordinal),
          recovery.SkippedPaths.Order(StringComparer.Ordinal).ToList(),
          materializedPaths.Order(StringComparer.Ordinal).ToList(),
          materializedTip);

      // Atomic replace: a crash mid-write must not leave a truncated document that
      // fails every later pull of this repository (#31).
      string path = GetRecoveryFilePath(repo);
      string temp = path + ".tmp";
      File.WriteAllText(temp, JsonSerializer.Serialize(document, s_recoveryJsonOptions));
      File.Move(temp, path, overwrite: true);
   }



   /// <summary>
   /// Reads .git\gclo-recovery.json. The file sits on disk between syncs, where
   /// anything can edit it, and its list of materialized paths decides what the next
   /// apply deletes: every entry must be a path inside the working directory, or the
   /// whole file is refused (#141). Renames and skips need no check here; they only
   /// take effect through the effective paths, which are validated before any write.
   /// </summary>
   private static RecoveryDocument LoadRecoveryDocument(string filePath, string workdir)
   {
      RecoveryDocument document;
      try
      {
         document = JsonSerializer.Deserialize<RecoveryDocument>(File.ReadAllText(filePath), s_recoveryJsonOptions)
             ?? throw new JsonException("the document is empty");
      }
      catch(JsonException ex)
      {
         throw new InvalidOperationException(
             $"The path-recovery file '{filePath}' is corrupt ({ex.Message}). "
             + "Delete it and use Resolve on this repository again to rebuild it.", ex);
      }

      foreach(string path in document.MaterializedPaths ?? [])
      {
         try
         {
            // Every path gclo ever recorded here had passed the validator and names a
            // file in the tree; one that does not was not written by gclo.
            if(string.IsNullOrEmpty(path) || WindowsPathValidator.ValidatePaths([path]).Count > 0)
            {
               throw new InvalidOperationException("not a path gclo could have written");
            }
            WorkingTree.Resolve(workdir, path);
         }
         catch(Exception ex) when(ex is InvalidOperationException or ArgumentException or IOException or NotSupportedException)
         {
            throw new InvalidOperationException(
                $"The path-recovery file '{filePath}' lists a file that is not in this repository's working tree ('{DisplayPath(path)}'). "
                + "Delete it and use Resolve on this repository again to rebuild it.", ex);
         }
      }

      return document;
   }



   /// <summary>A path from an untrusted file, cut to a length that fits a message.</summary>
   private static string DisplayPath(string? path)
       => path is null ? "" : path.Length <= 200 ? path : path[..200] + "...";



   /// <summary>
   /// The working directory path recovery writes into. A bare repository has none,
   /// and nothing gclo clones is bare: one that carries a recovery file was put
   /// together by hand.
   /// </summary>
   private static string WorkingDirectoryOf(Repository repo)
       => repo.Info.WorkingDirectory
           ?? throw new InvalidOperationException("This is a bare repository; it has no working tree to materialize.");



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
   /// The token is for <paramref name="expectedUrl"/>'s host only: a remote on any
   /// other host, or one reached over anything but HTTPS, is refused, so the token is
   /// never offered to a server the repository was not listed on (#139). libgit2 asks
   /// with the remote's own URL; a redirect the listed host itself issues is that
   /// host's doing, and it already holds the token.
   /// </summary>
   internal static CredentialsHandler MakeCredentialsProvider(string token, string expectedUrl)
   {
      int requests = 0;
      return (requestedUrl, _, _) =>
      {
         if(!RemoteUrl.MayReceiveCredentials(expectedUrl, requestedUrl))
         {
            throw new InvalidOperationException(
                  $"'{RemoteUrl.Redact(requestedUrl)}' asked for credentials, but the token is only sent over HTTPS "
                  + "to the host the repository was listed on. Nothing was sent.");
         }

         if(Interlocked.Increment(ref requests) > 2)
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
         if(!Directory.Exists(path))
         {
            return;
         }
         // Read-only pack files and all; never through a link inside the folder (a
         // checked-out symlink to a directory would otherwise lead the delete
         // elsewhere), see WorkingTree (#141).
         WorkingTree.DeleteDirectory(path);
      }
      catch
      {
         // Best effort only; a leftover partial clone surfaces as a failure on the next run.
      }
   }
}
