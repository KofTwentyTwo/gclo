using System.Diagnostics;
using System.Text;
using gclo.Engine;
using LibGit2Sharp;

namespace gclo.Engine.Tests;

/// <summary>
/// Shared fixture helpers: descriptor builders for fake repositories, polling waits,
/// and real on-disk git fixtures. Forged tree entries are written through the object
/// database because Windows-invalid names can never be created on a working tree
/// directly.
/// </summary>
internal static class GitTestHelpers
{
    /// <summary>Descriptor for a repository of the fake organization 'acme'.</summary>
    public static RepoDescriptor Repo(string name, string? branch = "main", bool archived = false)
        => new(name, $"https://example.test/acme/{name}.git", branch, archived);

    /// <summary>Descriptors (default branch, not archived) for the given names.</summary>
    public static RepoDescriptor[] Repos(params string[] names)
        => names.Select(name => Repo(name)).ToArray();

    /// <summary>Standard signature for fixture commits.</summary>
    public static Signature MakeSignature()
        => new("tester", "tester@example.test", DateTimeOffset.Now);

    /// <summary>Polls until <paramref name="condition"/> is true; fails the test after a generous timeout.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15),
                $"Timed out after {stopwatch.Elapsed} waiting for: {description}");
            await Task.Delay(10);
        }
    }

    /// <summary>SHA of the commit at HEAD's tip.</summary>
    public static string HeadSha(string workdir)
    {
        using var repo = new Repository(workdir);
        return repo.Head.Tip.Sha;
    }

    /// <summary>
    /// Builds a repository in a fresh directory under <paramref name="root"/> whose single
    /// commit holds the given forged entries with per-entry content; returns its path.
    /// </summary>
    public static string CreateForgedRepo(string root, params (string Name, string Content)[] entries)
    {
        string path = Path.Combine(root, "forged-" + Guid.NewGuid().ToString("N")[..8]);
        Repository.Init(path);
        using var repo = new Repository(path);

        var tree = repo.ObjectDatabase.CreateTree(AddForgedEntries(repo, new TreeDefinition(), entries));
        var signature = MakeSignature();
        var commit = repo.ObjectDatabase.CreateCommit(
            signature, signature, "forged commit", tree, [], prettifyMessage: false);
        repo.Refs.Add(repo.Refs.Head.TargetIdentifier, commit.Id);
        return path;
    }

    /// <summary>Convenience overload: every forged entry gets the same placeholder content.</summary>
    public static string CreateForgedRepo(string root, params string[] entryNames)
        => CreateForgedRepo(root, entryNames.Select(name => (name, "content")).ToArray());

    /// <summary>Adds a commit on top of HEAD writing (or overwriting) forged entries; returns its SHA.</summary>
    public static string AppendForgedCommit(string workdir, params (string Name, string Content)[] entries)
    {
        using var repo = new Repository(workdir);
        var head = repo.Head.Tip;

        var definition = AddForgedEntries(repo, TreeDefinition.From(head.Tree), entries);
        var tree = repo.ObjectDatabase.CreateTree(definition);
        var signature = MakeSignature();
        var commit = repo.ObjectDatabase.CreateCommit(
            signature, signature, "forged follow-up", tree, [head], prettifyMessage: false);
        repo.Refs.UpdateTarget(repo.Refs.Head.ResolveToDirectReference(), commit.Id);
        return commit.Sha;
    }

    private static TreeDefinition AddForgedEntries(
        Repository repo, TreeDefinition definition, (string Name, string Content)[] entries)
    {
        foreach (var (name, content) in entries)
        {
            var blob = repo.ObjectDatabase.CreateBlob(new MemoryStream(Encoding.UTF8.GetBytes(content)));
            definition.Add(name, blob, Mode.NonExecutableFile);
        }
        return definition;
    }

    /// <summary>
    /// Same approach as the production client: pack files under .git are written
    /// read-only on Windows, so clear attributes before deleting. Best effort — a
    /// stray temp dir is harmless.
    /// </summary>
    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
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
