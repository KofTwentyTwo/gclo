using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace gclo.Engine;

/// <summary>Git operations implemented with LibGit2Sharp.</summary>
public sealed class LibGit2GitClient : IGitClient
{
    public bool IsValidRepository(string path)
        => Directory.Exists(path) && Repository.IsValid(path);

    // LongRunning: each git operation blocks a thread for its whole duration; on the
    // thread pool, MaxConcurrency operations would starve the pool and delay ramp-up.
    public Task CloneAsync(string url, string path, string token, Action<double>? onProgress, CancellationToken cancellationToken)
        => Task.Factory.StartNew(
            () => Clone(url, path, token, onProgress, cancellationToken),
            cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public Task FetchAndPullAsync(string path, string token, CancellationToken cancellationToken)
        => Task.Factory.StartNew(
            () => FetchAndPull(path, token, cancellationToken),
            cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void Clone(string url, string path, string token, Action<double>? onProgress, CancellationToken ct)
    {
        bool existedBefore = Directory.Exists(path);

        var options = new CloneOptions
        {
            // Two-phase: fetch only, then checkout after core.longpaths is set and
            // every tree path is validated against Windows rules — a repo that is
            // fine on Linux must fail with a structured error, not a libgit2 one.
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
            // Lifts the 260-character path limit before anything touches the
            // working tree; libgit2 honors it for checkout.
            repo.Config.Set("core.longpaths", true, ConfigurationLevel.Local);

            var tip = repo.Head.Tip;
            if (tip is null)
            {
                return; // empty repository — nothing to check out
            }

            var invalidPaths = WindowsPathValidator.Validate(tip.Tree);
            if (invalidPaths.Count > 0)
            {
                throw new InvalidRepositoryPathsException(invalidPaths);
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
            // partial checkout as "exists locally" and try to pull it.
            if (!existedBefore)
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

        // Idempotent; also covers repositories that were cloned by other tools.
        repo.Config.Set("core.longpaths", true, ConfigurationLevel.Local);

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
        var invalidIncoming = WindowsPathValidator.Validate(tracked.Tip.Tree);
        if (invalidIncoming.Count > 0)
        {
            throw new InvalidRepositoryPathsException(invalidIncoming);
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

        var invalidPaths = WindowsPathValidator.Validate(remoteBranch.Tip.Tree);
        if (invalidPaths.Count > 0)
        {
            throw new InvalidRepositoryPathsException(invalidPaths);
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
