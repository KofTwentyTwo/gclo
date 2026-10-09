using gclo.Engine;
using LibGit2Sharp;
using static gclo.Engine.Tests.GitTestHelpers;

namespace gclo.Engine.Tests;

/// <summary>
/// Integration tests for <see cref="LibGit2GitClient"/> against real repositories on the
/// local file system. No network is involved: fixture repositories are created with
/// LibGit2Sharp and cloned/pushed through libgit2's local-path transport, which ignores
/// credentials — any non-empty token works.
/// </summary>
public sealed class LibGit2GitClientTests : IDisposable
{
    private const string Token = "test-token";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

    private readonly LibGit2GitClient _client = new();

    public LibGit2GitClientTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => TryDeleteDirectory(_root);

    // ---------------------------------------------------------------- clone

    [Fact]
    public async Task Clone_LocalSource_PopulatesWorkingTreeAndReportsProgressInRange()
    {
        string source = CreateSourceRepo();
        string target = NewPath("clone-target");
        var progressValues = new List<double>();

        await _client.CloneAsync(source, target, Token, progressValues.Add, CancellationToken.None);

        Assert.Equal("hello from source", File.ReadAllText(Path.Combine(target, "readme.txt")));
        Assert.True(_client.IsValidRepository(target));
        // Local-transport clones may fire few (or even zero) transfer callbacks, so do not
        // require that any arrived — only that every value is a valid completion fraction.
        Assert.All(progressValues, v => Assert.InRange(v, 0.0, 1.0));
    }

    [Fact]
    public async Task Clone_Completed_LeavesCheckoutPendingMarkerCleared()
    {
        string source = CreateSourceRepo();
        string target = NewPath("clone-target");

        await _client.CloneAsync(source, target, Token, null, CancellationToken.None);

        // The marker is set when the fetch lands and cleared only after checkout, so a
        // finished clone must read as "not pending" — the next pull trusts its tree.
        Assert.False(IsCheckoutPending(target));
    }

    [Fact]
    public async Task Clone_EmptyOrigin_LeavesCheckoutPendingMarkerCleared()
    {
        string origin = NewPath("bare-origin");
        Repository.Init(origin, isBare: true);
        string target = NewPath("unborn-clone");

        await _client.CloneAsync(origin, target, Token, null, CancellationToken.None);

        // Nothing to check out means nothing is pending; the unborn-HEAD pull path
        // (not the pending path) must handle this repo once the remote gets commits.
        Assert.False(IsCheckoutPending(target));
    }

    [Fact]
    public async Task Clone_SourceMissing_ThrowsAndLeavesNoTargetDirectory()
    {
        string missingSource = NewPath("no-such-source");
        string target = NewPath("failed-clone");

        await Assert.ThrowsAnyAsync<LibGit2SharpException>(
            () => _client.CloneAsync(missingSource, target, Token, null, CancellationToken.None));

        // A failed clone into a directory that did not exist before must clean up after
        // itself, or the next run would treat the leftovers as an existing local repo.
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task Clone_WindowsInvalidPath_ThrowsTypedExceptionAndKeepsRepoWithPendingMarker()
    {
        // A tree entry whose name is invalid on Windows must surface as the structured
        // InvalidRepositoryPathsException (not a raw libgit2 error). Unlike other clone
        // failures, the fetched repository is KEPT — everything is already downloaded,
        // so path recovery can run without hitting the network — and marked as
        // checkout-pending so a later pull cannot mistake it for an up-to-date clone.
        string source = CreateForgedRepo(_root, ("bad:name.txt", "unwritable on Windows"));
        string target = NewPath("partial-clone");

        var ex = await Assert.ThrowsAsync<InvalidRepositoryPathsException>(
            () => _client.CloneAsync(source, target, Token, null, CancellationToken.None));

        var info = Assert.Single(ex.Paths);
        Assert.Equal("bad:name.txt", info.RepoPath);
        Assert.Contains("invalid on Windows", info.Reason);
        Assert.Equal("bad_name.txt", info.SuggestedName);
        Assert.True(_client.IsValidRepository(target));
        using var repo = new Repository(target);
        Assert.True(repo.Config.Get<bool>("gclo.checkoutpending")?.Value);
    }

    [Fact]
    public async Task Clone_TokenAlreadyCanceled_ThrowsOperationCanceledAndCreatesNothing()
    {
        string source = CreateSourceRepo();
        string target = NewPath("canceled-clone");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _client.CloneAsync(source, target, Token, null, cts.Token));

        Assert.False(Directory.Exists(target));
    }

    // ---------------------------------------------------------------- IsValidRepository

    [Fact]
    public void IsValidRepository_MissingDirectory_ReturnsFalse()
    {
        Assert.False(_client.IsValidRepository(NewPath("does-not-exist")));
    }

    [Fact]
    public void IsValidRepository_EmptyDirectory_ReturnsFalse()
    {
        string path = NewPath("empty");
        Directory.CreateDirectory(path);

        Assert.False(_client.IsValidRepository(path));
    }

    [Fact]
    public void IsValidRepository_DirectoryWithUnrelatedFiles_ReturnsFalse()
    {
        string path = NewPath("not-a-repo");
        Directory.CreateDirectory(Path.Combine(path, "sub"));
        File.WriteAllText(Path.Combine(path, "notes.txt"), "just a file");
        File.WriteAllText(Path.Combine(path, "sub", "data.bin"), "more bytes");

        Assert.False(_client.IsValidRepository(path));
    }

    [Fact]
    public void IsValidRepository_RealRepository_ReturnsTrue()
    {
        string source = CreateSourceRepo();

        Assert.True(_client.IsValidRepository(source));
    }

    // ---------------------------------------------------------------- fetch & pull

    [Fact]
    public async Task FetchAndPull_RemoteAhead_FastForwardsToRemoteTip()
    {
        string source = CreateSourceRepo();
        string target = NewPath("clone");
        await _client.CloneAsync(source, target, Token, null, CancellationToken.None);

        string newSha = CommitFile(source, "update.txt", "new content", "second commit");

        await _client.FetchAndPullAsync(target, Token, CancellationToken.None);

        Assert.Equal(newSha, HeadSha(target));
        Assert.Equal("new content", File.ReadAllText(Path.Combine(target, "update.txt")));
    }

    [Fact]
    public async Task FetchAndPull_AlreadyUpToDate_CompletesAndLeavesTipUnchanged()
    {
        string source = CreateSourceRepo();
        string target = NewPath("clone");
        await _client.CloneAsync(source, target, Token, null, CancellationToken.None);
        string tipBefore = HeadSha(target);

        await _client.FetchAndPullAsync(target, Token, CancellationToken.None);

        Assert.Equal(tipBefore, HeadSha(target));
    }

    [Fact]
    public async Task FetchAndPull_IncompleteCloneLeftBehind_CompletesCheckoutInsteadOfReportingUpToDate()
    {
        // Simulates a clone whose checkout failed (or was killed) and whose cleanup
        // could not delete the directory: .git is complete and valid, the marker is
        // still set, and the working tree is partial. Commit tips are EQUAL, which is
        // exactly the case that used to pass as "already up to date".
        string source = CreateSourceRepo();
        string target = NewPath("half-clone");
        await _client.CloneAsync(source, target, Token, null, CancellationToken.None);
        string tipBefore = HeadSha(target);
        File.Delete(Path.Combine(target, "readme.txt"));
        SetCheckoutPending(target, true);
        Assert.True(_client.IsValidRepository(target));

        await _client.FetchAndPullAsync(target, Token, CancellationToken.None);

        Assert.Equal("hello from source", File.ReadAllText(Path.Combine(target, "readme.txt")));
        Assert.Equal(tipBefore, HeadSha(target));
        Assert.False(IsCheckoutPending(target));
    }

    [Fact]
    public async Task FetchAndPull_IncompleteCloneAndRemoteAhead_MaterializesTheNewTip()
    {
        string source = CreateSourceRepo();
        string target = NewPath("half-clone");
        await _client.CloneAsync(source, target, Token, null, CancellationToken.None);
        File.Delete(Path.Combine(target, "readme.txt"));
        SetCheckoutPending(target, true);
        string newSha = CommitFile(source, "update.txt", "new content", "second commit");

        await _client.FetchAndPullAsync(target, Token, CancellationToken.None);

        Assert.Equal(newSha, HeadSha(target));
        Assert.Equal("hello from source", File.ReadAllText(Path.Combine(target, "readme.txt")));
        Assert.Equal("new content", File.ReadAllText(Path.Combine(target, "update.txt")));
        Assert.False(IsCheckoutPending(target));
    }

    [Fact]
    public async Task FetchAndPull_IncompleteCloneWhoseTipIsNowInvalidOnWindows_FailsTypedNotUpToDate()
    {
        // The pending path re-validates before checking out: an incomplete clone whose
        // tree gained a Windows-invalid path must surface the structured failure, not
        // a silent "Done" with a partial tree. (Forged entries cannot be created on a
        // Windows working tree, which is why this uses the object-database fixture.)
        string source = CreateSourceRepo();
        string target = NewPath("half-clone");
        await _client.CloneAsync(source, target, Token, null, CancellationToken.None);
        SetCheckoutPending(target, true);
        AppendForgedCommit(source, ("bad:name.txt", "unwritable on Windows"));

        if (!OperatingSystem.IsWindows())
        {
            return; // the validator only runs on Windows
        }

        var ex = await Assert.ThrowsAsync<InvalidRepositoryPathsException>(
            () => _client.FetchAndPullAsync(target, Token, CancellationToken.None));

        Assert.Equal("bad:name.txt", Assert.Single(ex.Paths).RepoPath);
        Assert.True(IsCheckoutPending(target), "still incomplete; the marker must survive the failure");
    }

    [Fact]
    public async Task FetchAndPull_DivergedHistories_ThrowsNonFastForward()
    {
        string source = CreateSourceRepo();
        string target = NewPath("clone");
        await _client.CloneAsync(source, target, Token, null, CancellationToken.None);

        // Both sides advance from the shared base: no fast-forward is possible, and a
        // mirror tool must never manufacture a merge commit.
        CommitFile(source, "from-remote.txt", "remote change", "remote commit");
        CommitFile(target, "from-local.txt", "local change", "local commit");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _client.FetchAndPullAsync(target, Token, CancellationToken.None));

        // Actionable, not libgit2's "cannot fast-forward": names the branch, says what
        // gclo refuses to do, and gives the three ways out (#30).
        Assert.Contains("local commits that origin does not have", ex.Message);
        Assert.Contains("never merges", ex.Message);
        Assert.Contains("re-clone", ex.Message);
        Assert.IsType<NonFastForwardException>(ex.InnerException);
    }

    [Fact]
    public async Task FetchAndPull_DetachedHead_ThrowsInvalidOperationMentioningDetached()
    {
        string source = CreateSourceRepo();
        string target = NewPath("clone");
        await _client.CloneAsync(source, target, Token, null, CancellationToken.None);

        using (var repo = new Repository(target))
        {
            Commands.Checkout(repo, repo.Head.Tip.Sha);
            Assert.True(repo.Info.IsHeadDetached);
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _client.FetchAndPullAsync(target, Token, CancellationToken.None));

        Assert.Contains("detached", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Check out a branch", ex.Message);
    }

    [Fact]
    public async Task FetchAndPull_BranchWithoutUpstream_FetchesButMergesNothing()
    {
        string source = CreateSourceRepo();
        string target = NewPath("clone");
        await _client.CloneAsync(source, target, Token, null, CancellationToken.None);
        string tipBefore = HeadSha(target);

        string defaultBranch;
        using (var repo = new Repository(target))
        {
            defaultBranch = repo.Head.FriendlyName;
            var localOnly = repo.CreateBranch("local-only");
            Commands.Checkout(repo, localOnly);
            Assert.Null(repo.Head.TrackedBranch);
        }

        string remoteSha = CommitFile(source, "later.txt", "later content", "remote commit");

        await _client.FetchAndPullAsync(target, Token, CancellationToken.None);

        using var after = new Repository(target);
        Assert.Equal("local-only", after.Head.FriendlyName);
        Assert.Equal(tipBefore, after.Head.Tip.Sha); // nothing was merged...
        Assert.Equal(remoteSha, after.Branches["origin/" + defaultBranch].Tip.Sha); // ...but the fetch ran
        Assert.False(File.Exists(Path.Combine(target, "later.txt")));
    }

    [Fact]
    public async Task FetchAndPull_UnbornHead_MaterializesBranchUpstreamAndWorkingTree()
    {
        // A clone of an empty origin leaves HEAD unborn: pointing at a branch with no commits.
        string origin = NewPath("bare-origin");
        Repository.Init(origin, isBare: true);

        string target = NewPath("unborn-clone");
        await _client.CloneAsync(origin, target, Token, null, CancellationToken.None);
        using (var repo = new Repository(target))
        {
            Assert.True(repo.Info.IsHeadUnborn);
        }

        // Seed the origin through a third working copy — a local push needs no credentials.
        string seeder = NewPath("seeder");
        await _client.CloneAsync(origin, seeder, Token, null, CancellationToken.None);
        string seededSha = CommitFile(seeder, "seed.txt", "seeded content", "initial commit");
        using (var repo = new Repository(seeder))
        {
            repo.Network.Push(repo.Network.Remotes["origin"], repo.Head.CanonicalName, new PushOptions());
        }

        await _client.FetchAndPullAsync(target, Token, CancellationToken.None);

        using var after = new Repository(target);
        Assert.False(after.Info.IsHeadUnborn);
        Assert.Equal(seededSha, after.Head.Tip.Sha);
        Assert.NotNull(after.Head.TrackedBranch);
        Assert.Equal("seeded content", File.ReadAllText(Path.Combine(target, "seed.txt")));
    }

    [Fact]
    public async Task FetchAndPull_UnbornHeadAndRemoteStillEmpty_ReturnsWithoutError()
    {
        string origin = NewPath("bare-origin");
        Repository.Init(origin, isBare: true);

        string target = NewPath("unborn-clone");
        await _client.CloneAsync(origin, target, Token, null, CancellationToken.None);

        // Nothing to pull yet: there is no branch to materialize, and that is not an error.
        await _client.FetchAndPullAsync(target, Token, CancellationToken.None);

        using var after = new Repository(target);
        Assert.True(after.Info.IsHeadUnborn);
    }

    // ---------------------------------------------------------------- fixture helpers

    private string NewPath(string name) => Path.Combine(_root, name);

    private static bool IsCheckoutPending(string workdir)
    {
        using var repo = new Repository(workdir);
        return repo.Config.Get<bool>("gclo.checkoutpending")?.Value == true;
    }

    private static void SetCheckoutPending(string workdir, bool value)
    {
        using var repo = new Repository(workdir);
        repo.Config.Set("gclo.checkoutpending", value, ConfigurationLevel.Local);
    }

    /// <summary>Initializes a non-bare repository under the test root with one commit.</summary>
    private string CreateSourceRepo(string name = "source")
    {
        string path = NewPath(name);
        Repository.Init(path);
        CommitFile(path, "readme.txt", "hello from source", "initial commit");
        return path;
    }

    /// <summary>Writes a file into the working tree, stages it, and commits; returns the new SHA.</summary>
    private static string CommitFile(string workdir, string fileName, string content, string message)
    {
        using var repo = new Repository(workdir);
        File.WriteAllText(Path.Combine(workdir, fileName), content);
        Commands.Stage(repo, fileName);
        var signature = MakeSignature();
        return repo.Commit(message, signature, signature).Sha;
    }
}
