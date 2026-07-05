using gclo.Engine;
using LibGit2Sharp;
using static gclo.Engine.Tests.GitTestHelpers;

namespace gclo.Engine.Tests;

/// <summary>
/// Tests for Windows path validation on clone/pull: paths that are legal in git but
/// invalid on Windows must surface as a structured <see cref="InvalidRepositoryPathsException"/>
/// before checkout, and long-but-valid paths must clone successfully (core.longpaths).
/// Offending tree entries are forged through the object database — they can never be
/// created on a Windows working tree directly.
/// </summary>
public sealed class WindowsPathValidationTests : IDisposable
{
    private const string Token = "test-token";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

    private readonly LibGit2GitClient _client = new();

    public WindowsPathValidationTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => TryDeleteDirectory(_root);

    // ---------------------------------------------------------------- validator

    [Theory]
    [InlineData("trailing-space ", "trailing space or dot", "trailing-space")]
    [InlineData("trailing-dot.", "trailing space or dot", "trailing-dot")]
    [InlineData("bad:colon.txt", "invalid on Windows", "bad_colon.txt")]
    [InlineData("CON", "reserved Windows device name", "CON_")]
    [InlineData("COM3.txt", "reserved Windows device name", "COM3.txt_")]
    public void Validate_FlagsInvalidSegment_WithReasonAndSuggestion(
        string entryName, string expectedReasonFragment, string expectedSuggestion)
    {
        var invalid = ValidateNames(entryName);

        var info = Assert.Single(invalid);
        Assert.Equal(entryName, info.RepoPath);
        Assert.Contains(expectedReasonFragment, info.Reason);
        Assert.Equal(expectedSuggestion, info.SuggestedName);
    }

    [Fact]
    public void Validate_FlagsCaseOnlyCollision()
    {
        var invalid = ValidateNames("README.md", "readme.md");

        var info = Assert.Single(invalid);
        Assert.Contains("differs only by case", info.Reason);
    }

    [Fact]
    public void Validate_FlagsOverlongSegment()
    {
        var invalid = ValidateNames(new string('x', 256));

        var info = Assert.Single(invalid);
        Assert.Contains("longer than 255", info.Reason);
    }

    [Fact]
    public void Validate_FlagsInvalidDirectorySegment_InNestedPath()
    {
        var invalid = ValidateNames("bad-dir /nested.txt");

        var info = Assert.Single(invalid);
        Assert.Equal("bad-dir ", info.RepoPath);
        Assert.Contains("trailing space or dot", info.Reason);
    }

    [Fact]
    public void Validate_ValidTree_ReturnsEmpty()
    {
        var invalid = ValidateNames("readme.txt", "src/main.cs", ".github/workflows/ci.yml", "a.b.c");

        Assert.Empty(invalid);
    }

    [Fact]
    public void Validate_EmptySubtree_YieldsTheDirectoryPath()
    {
        // A tree can carry an empty subtree (git normally cannot, but a forged tree
        // or a filter can): the flattener yields the directory itself so an invalid
        // directory name is still validated. A plain "emptydir" is valid, so the
        // result is empty — but the empty-subtree branch is exercised.
        string path = NewPath("empty-subtree-" + Guid.NewGuid().ToString("N")[..8]);
        Repository.Init(path, isBare: true);
        using var repo = new Repository(path);

        var emptyTree = repo.ObjectDatabase.CreateTree(new TreeDefinition());
        var definition = new TreeDefinition();
        definition.Add("emptydir", emptyTree);

        var invalid = WindowsPathValidator.Validate(repo.ObjectDatabase.CreateTree(definition));

        Assert.Empty(invalid); // "emptydir" is a valid name; the branch still ran
    }

    [Fact]
    public void Validate_EmptySubtreeWithInvalidName_IsFlagged()
    {
        string path = NewPath("empty-badsubtree-" + Guid.NewGuid().ToString("N")[..8]);
        Repository.Init(path, isBare: true);
        using var repo = new Repository(path);

        var emptyTree = repo.ObjectDatabase.CreateTree(new TreeDefinition());
        var definition = new TreeDefinition();
        definition.Add("bad ", emptyTree); // trailing space on the empty directory

        var invalid = WindowsPathValidator.Validate(repo.ObjectDatabase.CreateTree(definition));

        Assert.Contains(invalid, i => i.RepoPath == "bad " && i.Reason.Contains("trailing space"));
    }

    // ---------------------------------------------------------------- clone behavior

    [Fact]
    public async Task Clone_MixedValidAndInvalidPaths_ReportsOnlyInvalidOnes_AndKeepsFetchedRepo()
    {
        string source = CreateForgedRepo(_root, "good.txt", "bad:name.txt", "NUL.log");
        string target = NewPath("mixed-clone");

        var ex = await Assert.ThrowsAsync<InvalidRepositoryPathsException>(
            () => _client.CloneAsync(source, target, Token, null, CancellationToken.None));

        Assert.Equal(2, ex.Paths.Count);
        Assert.DoesNotContain(ex.Paths, p => p.RepoPath == "good.txt");
        // The fetched repo is kept for path recovery (marked checkout-pending),
        // but nothing may have been checked out.
        Assert.True(_client.IsValidRepository(target));
        Assert.False(File.Exists(Path.Combine(target, "good.txt")));
        using var repo = new Repository(target);
        Assert.True(repo.Config.Get<bool>("gclo.checkoutpending")?.Value);
    }

    [Fact]
    public async Task Clone_PathLongerThanLegacyMaxPath_Succeeds()
    {
        // Regression for owner-reported "filename or folder too long" failures:
        // core.longpaths is set before checkout, so a deep-but-valid tree clones fine.
        string segment = new string('d', 60);
        string repoPath = string.Join('/', Enumerable.Repeat(segment, 4)) + "/deep-file.txt";
        string source = CreateSourceRepoWithLongPath(repoPath);
        string target = NewPath("long-clone");
        Assert.True((target + "/" + repoPath).Length > 260); // the scenario is real

        await _client.CloneAsync(source, target, Token, null, CancellationToken.None);

        string fullPath = Path.Combine(target, repoPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal("deep content", File.ReadAllText(fullPath));
    }

    [Fact]
    public async Task FetchAndPull_IncomingCommitWithInvalidPath_ThrowsTypedException()
    {
        string source = CreateForgedRepo(_root, "original.txt");
        string target = NewPath("pull-target");
        await _client.CloneAsync(source, target, Token, null, CancellationToken.None);

        AppendForgedCommit(source, ("bad:incoming.txt", "x"));

        var ex = await Assert.ThrowsAsync<InvalidRepositoryPathsException>(
            () => _client.FetchAndPullAsync(target, Token, CancellationToken.None));

        Assert.Contains(ex.Paths, p => p.RepoPath == "bad:incoming.txt");
        // The valid original working tree must be untouched.
        Assert.Equal("content", File.ReadAllText(Path.Combine(target, "original.txt")));
    }

    // ---------------------------------------------------------------- fixture helpers

    private string NewPath(string name) => Path.Combine(_root, name);

    /// <summary>Runs the validator over a tree containing exactly the given entry names.</summary>
    private IReadOnlyList<InvalidPathInfo> ValidateNames(params string[] entryNames)
    {
        string path = NewPath("validator-" + Guid.NewGuid().ToString("N")[..8]);
        Repository.Init(path, isBare: true);
        using var repo = new Repository(path);

        var blob = repo.ObjectDatabase.CreateBlob(new MemoryStream("x"u8.ToArray()));
        var definition = new TreeDefinition();
        foreach (string name in entryNames)
        {
            definition.Add(name, blob, Mode.NonExecutableFile);
        }
        return WindowsPathValidator.Validate(repo.ObjectDatabase.CreateTree(definition));
    }

    /// <summary>Creates a repo containing one file at a deep path exceeding legacy MAX_PATH.</summary>
    private string CreateSourceRepoWithLongPath(string repoPath)
    {
        string path = NewPath("long-source");
        Repository.Init(path);
        using var repo = new Repository(path);
        // The fixture itself needs long-path support to stage the file.
        repo.Config.Set("core.longpaths", true, ConfigurationLevel.Local);

        string fullPath = Path.Combine(path, repoPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "deep content");
        Commands.Stage(repo, repoPath);
        var signature = MakeSignature();
        repo.Commit("long path commit", signature, signature);
        return path;
    }
}
