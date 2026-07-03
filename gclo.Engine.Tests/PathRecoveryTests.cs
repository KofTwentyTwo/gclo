using System.Text;
using gclo.Engine;
using LibGit2Sharp;

namespace gclo.Engine.Tests;

/// <summary>
/// Tests for path recovery: a clone whose tree contains Windows-invalid paths keeps
/// the fetched repository (marked checkout-pending) instead of deleting it, and
/// <see cref="IGitClient.ApplyRecoveryAsync"/> materializes the working tree manually
/// with user-chosen renames and skips applied. The recovery persists under .git and
/// is re-applied by later pulls. Offending tree entries are forged through the object
/// database — they can never be created on a Windows working tree directly.
/// </summary>
public sealed class PathRecoveryTests : IDisposable
{
    private const string Token = "test-token";
    private const string PendingKey = "gclo.checkoutpending";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

    private readonly LibGit2GitClient _client = new();

    public PathRecoveryTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => TryDeleteDirectory(_root);

    // ---------------------------------------------------------------- clone marker

    [Fact]
    public async Task Clone_InvalidPaths_KeepsFetchedRepo_SetsPendingMarker_AndThrowsTyped()
    {
        string source = CreateForgedRepo(("good.txt", "good content"), ("bad:file.txt", "bad content"));
        string target = NewPath("marker-clone");

        var ex = await Assert.ThrowsAsync<InvalidRepositoryPathsException>(
            () => _client.CloneAsync(source, target, Token, null, CancellationToken.None));

        Assert.Contains(ex.Paths, p => p.RepoPath == "bad:file.txt");
        Assert.True(_client.IsValidRepository(target));
        Assert.True(IsCheckoutPending(target));
        // Nothing may have been checked out, and no recovery exists yet.
        Assert.False(File.Exists(Path.Combine(target, "good.txt")));
        Assert.False(File.Exists(RecoveryFilePath(target)));
    }

    // ---------------------------------------------------------------- apply recovery

    [Fact]
    public async Task ApplyRecovery_RenamedFile_MaterializesContentUnderNewName()
    {
        string source = CreateForgedRepo(("good.txt", "good content"), ("bad:file.txt", "bad content"));
        string target = await CloneExpectingInvalidPathsAsync(source, "rename-apply");

        var recovery = Recovery(renames: [("bad:file.txt", "bad_file.txt")]);
        await _client.ApplyRecoveryAsync(target, Token, recovery, CancellationToken.None);

        Assert.Equal("good content", File.ReadAllText(Path.Combine(target, "good.txt")));
        Assert.Equal("bad content", File.ReadAllText(Path.Combine(target, "bad_file.txt")));
    }

    [Fact]
    public async Task ApplyRecovery_RenamedDirectory_RelocatesItsWholeSubtree()
    {
        string source = CreateForgedRepo(
            ("bad:dir/inner.txt", "inner content"),
            ("bad:dir/sub/deep.txt", "deep content"));
        string target = await CloneExpectingInvalidPathsAsync(source, "dir-rename");

        var recovery = Recovery(renames: [("bad:dir", "bad_dir")]);
        await _client.ApplyRecoveryAsync(target, Token, recovery, CancellationToken.None);

        Assert.Equal("inner content", File.ReadAllText(Path.Combine(target, "bad_dir", "inner.txt")));
        Assert.Equal("deep content", File.ReadAllText(Path.Combine(target, "bad_dir", "sub", "deep.txt")));
    }

    [Fact]
    public async Task ApplyRecovery_SkippedPaths_OmitFilesAndWholeDirectories()
    {
        string source = CreateForgedRepo(
            ("bad:one.txt", "unwanted"),
            ("good.txt", "kept"),
            ("junk/a.txt", "junk a"),
            ("junk/sub/b.txt", "junk b"),
            ("docs/keep.txt", "docs kept"));
        string target = await CloneExpectingInvalidPathsAsync(source, "skip-apply");

        // Skip the invalid file outright and a whole (valid) directory subtree.
        var recovery = Recovery(skips: ["bad:one.txt", "junk"]);
        await _client.ApplyRecoveryAsync(target, Token, recovery, CancellationToken.None);

        Assert.Equal("kept", File.ReadAllText(Path.Combine(target, "good.txt")));
        Assert.Equal("docs kept", File.ReadAllText(Path.Combine(target, "docs", "keep.txt")));
        Assert.False(Directory.Exists(Path.Combine(target, "junk")));
        Assert.False(IsCheckoutPending(target));
    }

    [Fact]
    public async Task ApplyRecovery_MappingStillInvalid_ThrowsListingEffectivePaths_AndWritesNothing()
    {
        string source = CreateForgedRepo(("good.txt", "good content"), ("bad:file.txt", "bad content"));
        string target = await CloneExpectingInvalidPathsAsync(source, "still-invalid");

        var recovery = Recovery(renames: [("bad:file.txt", "still:bad.txt")]);
        var ex = await Assert.ThrowsAsync<InvalidRepositoryPathsException>(
            () => _client.ApplyRecoveryAsync(target, Token, recovery, CancellationToken.None));

        // The exception lists the EFFECTIVE (post-mapping) path, not the original.
        Assert.Contains(ex.Paths, p => p.RepoPath == "still:bad.txt");
        // Validation runs before any write: even the valid file must not appear,
        // the marker stays pending, and no recovery is persisted.
        Assert.False(File.Exists(Path.Combine(target, "good.txt")));
        Assert.True(IsCheckoutPending(target));
        Assert.False(File.Exists(RecoveryFilePath(target)));
    }

    [Fact]
    public async Task ApplyRecovery_TwoOriginalsMappedToOneDestination_Throws()
    {
        string source = CreateForgedRepo(("bad:a.txt", "content a"), ("bad:b.txt", "content b"));
        string target = await CloneExpectingInvalidPathsAsync(source, "collision");

        var recovery = Recovery(renames: [("bad:a.txt", "merged.txt"), ("bad:b.txt", "merged.txt")]);
        var ex = await Assert.ThrowsAsync<InvalidRepositoryPathsException>(
            () => _client.ApplyRecoveryAsync(target, Token, recovery, CancellationToken.None));

        Assert.Contains(ex.Paths, p => p.RepoPath == "merged.txt");
        Assert.False(File.Exists(Path.Combine(target, "merged.txt")));
    }

    [Fact]
    public async Task ApplyRecovery_PersistsRecoveryJsonUnderDotGit_AndClearsMarker()
    {
        string source = CreateForgedRepo(("bad:file.txt", "bad content"));
        string target = await CloneExpectingInvalidPathsAsync(source, "persist");

        var recovery = Recovery(renames: [("bad:file.txt", "bad_file.txt")]);
        await _client.ApplyRecoveryAsync(target, Token, recovery, CancellationToken.None);

        Assert.False(IsCheckoutPending(target));
        string json = File.ReadAllText(RecoveryFilePath(target));
        Assert.Contains("bad:file.txt", json);
        Assert.Contains("bad_file.txt", json);
    }

    // ---------------------------------------------------------------- pull semantics

    [Fact]
    public async Task FetchAndPull_MarkerRepoWithoutRecovery_RethrowsTypedException()
    {
        string source = CreateForgedRepo(("bad:file.txt", "bad content"));
        string target = await CloneExpectingInvalidPathsAsync(source, "marker-pull");

        // The repo must not silently report success with an empty working tree.
        var ex = await Assert.ThrowsAsync<InvalidRepositoryPathsException>(
            () => _client.FetchAndPullAsync(target, Token, CancellationToken.None));

        Assert.Contains(ex.Paths, p => p.RepoPath == "bad:file.txt");
        Assert.True(IsCheckoutPending(target));
    }

    [Fact]
    public async Task FetchAndPull_MarkerRepoUpstreamFixedPaths_ChecksOutAndClearsMarker()
    {
        string source = CreateForgedRepo(("good.txt", "good content"), ("bad:file.txt", "bad content"));
        string target = await CloneExpectingInvalidPathsAsync(source, "fixed-upstream");

        ReplaceForgedEntry(source, remove: "bad:file.txt", add: ("fixed.txt", "fixed content"));

        await _client.FetchAndPullAsync(target, Token, CancellationToken.None);

        Assert.Equal("good content", File.ReadAllText(Path.Combine(target, "good.txt")));
        Assert.Equal("fixed content", File.ReadAllText(Path.Combine(target, "fixed.txt")));
        Assert.False(IsCheckoutPending(target));
        Assert.Equal(HeadSha(source), HeadSha(target));
    }

    [Fact]
    public async Task FetchAndPull_WithStoredRecovery_RematerializesNewCommitsThroughMapping()
    {
        string source = CreateForgedRepo(("bad:file.txt", "v1"), ("good.txt", "g1"));
        string target = await CloneExpectingInvalidPathsAsync(source, "recovery-pull");
        await _client.ApplyRecoveryAsync(
            target, Token, Recovery(renames: [("bad:file.txt", "bad_file.txt")]), CancellationToken.None);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(target, "bad_file.txt")));

        // Upstream rewrites the still-invalid file; the pull must land the new
        // content at the mapped name.
        string newSha = AppendForgedCommit(source, ("bad:file.txt", "v2"));

        await _client.FetchAndPullAsync(target, Token, CancellationToken.None);

        Assert.Equal("v2", File.ReadAllText(Path.Combine(target, "bad_file.txt")));
        Assert.Equal("g1", File.ReadAllText(Path.Combine(target, "good.txt")));
        Assert.Equal(newSha, HeadSha(target));
    }

    [Fact]
    public async Task FetchAndPull_WithStoredRecovery_NewInvalidPathNotCovered_Throws()
    {
        string source = CreateForgedRepo(("bad:file.txt", "v1"));
        string target = await CloneExpectingInvalidPathsAsync(source, "uncovered-pull");
        await _client.ApplyRecoveryAsync(
            target, Token, Recovery(renames: [("bad:file.txt", "bad_file.txt")]), CancellationToken.None);

        AppendForgedCommit(source, ("worse:new.txt", "not covered"));

        var ex = await Assert.ThrowsAsync<InvalidRepositoryPathsException>(
            () => _client.FetchAndPullAsync(target, Token, CancellationToken.None));

        Assert.Contains(ex.Paths, p => p.RepoPath == "worse:new.txt");
        // The previously materialized working tree is untouched.
        Assert.Equal("v1", File.ReadAllText(Path.Combine(target, "bad_file.txt")));
    }

    [Fact]
    public async Task CloneAndPull_ValidRepo_NeverWritesMarkerOrRecovery()
    {
        string source = CreateForgedRepo(("readme.txt", "hello"));
        string target = NewPath("normal");

        await _client.CloneAsync(source, target, Token, null, CancellationToken.None);
        Assert.False(IsCheckoutPending(target));
        Assert.False(File.Exists(RecoveryFilePath(target)));

        await _client.FetchAndPullAsync(target, Token, CancellationToken.None);
        Assert.False(IsCheckoutPending(target));
        Assert.False(File.Exists(RecoveryFilePath(target)));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "readme.txt")));
    }

    // ---------------------------------------------------------------- engine payload

    [Fact]
    public async Task OrgSync_InvalidPathFailure_AttachesInvalidPathsToFailedReport()
    {
        var lister = new FakeRepositoryLister
        {
            Repositories = [new RepoDescriptor("broken", "https://example.test/acme/broken.git", "main", IsArchived: false)],
        };
        var git = new FakeGitClient
        {
            CloneHandler = (_, _, _, _, _) => Task.FromException(new InvalidRepositoryPathsException(
                [new InvalidPathInfo("bad:file.txt", "contains a character that is invalid on Windows", "bad_file.txt")])),
        };
        var progress = new RecordingProgress();

        var summary = await new OrgSyncEngine(lister, git)
            .SyncAsync(new SyncRequest("acme", Token, NewPath("org-root")), progress);

        Assert.Equal(1, summary.Failed);
        var report = progress.LastFor("broken");
        Assert.NotNull(report);
        Assert.Equal(SyncStatus.Failed, report!.Status);
        Assert.NotNull(report.InvalidPaths);
        var info = Assert.Single(report.InvalidPaths!);
        Assert.Equal("bad:file.txt", info.RepoPath);
        Assert.Equal("bad_file.txt", info.SuggestedName);
    }

    [Fact]
    public async Task OrgSync_OtherFailures_AttachNoInvalidPathsPayload()
    {
        var lister = new FakeRepositoryLister
        {
            Repositories = [new RepoDescriptor("flaky", "https://example.test/acme/flaky.git", "main", IsArchived: false)],
        };
        var git = new FakeGitClient
        {
            CloneHandler = (_, _, _, _, _) => Task.FromException(new InvalidOperationException("boom")),
        };
        var progress = new RecordingProgress();

        await new OrgSyncEngine(lister, git)
            .SyncAsync(new SyncRequest("acme", Token, NewPath("org-root-2")), progress);

        var report = progress.LastFor("flaky");
        Assert.NotNull(report);
        Assert.Equal(SyncStatus.Failed, report!.Status);
        Assert.Null(report.InvalidPaths);
    }

    // ---------------------------------------------------------------- fixture helpers

    private string NewPath(string name) => Path.Combine(_root, name);

    /// <summary>Clones a repo known to hold invalid paths, asserting the typed failure; returns the kept target.</summary>
    private async Task<string> CloneExpectingInvalidPathsAsync(string source, string name)
    {
        string target = NewPath(name);
        await Assert.ThrowsAsync<InvalidRepositoryPathsException>(
            () => _client.CloneAsync(source, target, Token, null, CancellationToken.None));
        return target;
    }

    private static PathRecovery Recovery(
        (string From, string To)[]? renames = null,
        string[]? skips = null)
        => new(
            (renames ?? []).ToDictionary(r => r.From, r => r.To, StringComparer.Ordinal),
            new HashSet<string>(skips ?? [], StringComparer.Ordinal));

    /// <summary>Builds a repo whose single commit holds the given forged entries with per-entry content.</summary>
    private string CreateForgedRepo(params (string Name, string Content)[] entries)
    {
        string path = NewPath("forged-" + Guid.NewGuid().ToString("N")[..8]);
        Repository.Init(path);
        using var repo = new Repository(path);

        var definition = new TreeDefinition();
        foreach (var (name, content) in entries)
        {
            var blob = repo.ObjectDatabase.CreateBlob(new MemoryStream(Encoding.UTF8.GetBytes(content)));
            definition.Add(name, blob, Mode.NonExecutableFile);
        }
        var tree = repo.ObjectDatabase.CreateTree(definition);
        var signature = MakeSignature();
        var commit = repo.ObjectDatabase.CreateCommit(
            signature, signature, "forged commit", tree, [], prettifyMessage: false);
        repo.Refs.Add(repo.Refs.Head.TargetIdentifier, commit.Id);
        return path;
    }

    /// <summary>Adds a commit on top of HEAD writing (or overwriting) forged entries; returns its SHA.</summary>
    private static string AppendForgedCommit(string workdir, params (string Name, string Content)[] entries)
    {
        using var repo = new Repository(workdir);
        var head = repo.Head.Tip;

        var definition = TreeDefinition.From(head.Tree);
        foreach (var (name, content) in entries)
        {
            var blob = repo.ObjectDatabase.CreateBlob(new MemoryStream(Encoding.UTF8.GetBytes(content)));
            definition.Add(name, blob, Mode.NonExecutableFile);
        }
        var tree = repo.ObjectDatabase.CreateTree(definition);
        var signature = MakeSignature();
        var commit = repo.ObjectDatabase.CreateCommit(
            signature, signature, "forged follow-up", tree, [head], prettifyMessage: false);
        repo.Refs.UpdateTarget(repo.Refs.Head.ResolveToDirectReference(), commit.Id);
        return commit.Sha;
    }

    /// <summary>Adds a commit that removes one forged entry and adds another — "upstream fixed the path".</summary>
    private static void ReplaceForgedEntry(string workdir, string remove, (string Name, string Content) add)
    {
        using var repo = new Repository(workdir);
        var head = repo.Head.Tip;

        var definition = TreeDefinition.From(head.Tree).Remove(remove);
        var blob = repo.ObjectDatabase.CreateBlob(new MemoryStream(Encoding.UTF8.GetBytes(add.Content)));
        definition.Add(add.Name, blob, Mode.NonExecutableFile);
        var tree = repo.ObjectDatabase.CreateTree(definition);
        var signature = MakeSignature();
        var commit = repo.ObjectDatabase.CreateCommit(
            signature, signature, "forged fix", tree, [head], prettifyMessage: false);
        repo.Refs.UpdateTarget(repo.Refs.Head.ResolveToDirectReference(), commit.Id);
    }

    private static Signature MakeSignature()
        => new("tester", "tester@example.test", DateTimeOffset.Now);

    private static bool IsCheckoutPending(string repoPath)
    {
        using var repo = new Repository(repoPath);
        return repo.Config.Get<bool>(PendingKey)?.Value == true;
    }

    private static string RecoveryFilePath(string repoPath)
    {
        using var repo = new Repository(repoPath);
        return Path.Combine(repo.Info.Path, "gclo-recovery.json");
    }

    private static string HeadSha(string workdir)
    {
        using var repo = new Repository(workdir);
        return repo.Head.Tip.Sha;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort; stray temp dirs are harmless.
        }
    }
}
